# ADR-010 — OpenAI API Completeness

**Status:** Accepted
**Date:** 2026-06-12

---

## Context

InferRouter exposes an OpenAI-compatible `/v1/chat/completions` endpoint, but the current implementation covers only a subset of the OpenAI API surface. Clients that use tool calling, multimodal input, extended sampling parameters, or the `/v1/models` discovery endpoint cannot use InferRouter as a drop-in replacement for OpenAI or OpenRouter without modification.

The goal of this ADR is to close the gap such that any project written against the OpenAI Python or JavaScript SDK can be pointed at InferRouter with only a base URL swap, with no code changes.

Inbound request authentication (API key validation) is explicitly out of scope — InferRouter is a self-hosted, network-isolated service and does not validate caller identity.

---

## Decision

The following capabilities are added in this release:

### 1. Tool calling / function calling

Tool definitions and tool results are passed through transparently. InferRouter does not interpret, validate, or transform tool definitions — it serialises them and forwards them to the upstream provider as-is.

**`InferRequest`** gains two new optional fields:

```csharp
public record InferRequest(
    string RequestId,
    IReadOnlyList<ChatMessage> Messages,
    string? Model,
    int? MaxTokens,
    float? Temperature,
    float? TopP,                          // new (ADR-010)
    float? FrequencyPenalty,              // new (ADR-010)
    float? PresencePenalty,               // new (ADR-010)
    IReadOnlyList<ToolDefinition>? Tools, // new (ADR-010)
    string? ToolChoice                    // new (ADR-010)
);
```

**`ChatMessage`** gains two new optional fields to support the `tool` role and assistant messages carrying tool calls:

```csharp
public record ChatMessage(
    string Role,
    string? Content,                             // nullable: tool call assistant messages may have null content
    string? ToolCallId = null,                   // new (ADR-010): present on role=tool messages
    IReadOnlyList<ToolCall>? ToolCalls = null    // new (ADR-010): present on role=assistant with tool calls
);
```

**New domain types:**

```csharp
public record ToolDefinition(
    string Type,           // always "function"
    ToolFunction Function
);

public record ToolFunction(
    string Name,
    string? Description,
    JsonElement? Parameters  // JSON Schema object; passed through as-is
);

public record ToolCall(
    string Id,
    string Type,           // always "function"
    ToolCallFunction Function
);

public record ToolCallFunction(
    string Name,
    string Arguments       // raw JSON string
);
```

**`InferResult`** gains an optional `ToolCalls` field for non-streaming responses:

```csharp
public record InferResult(
    string RequestId,
    string ProviderName,
    string Model,
    string? Content,                            // nullable: tool call responses have null content
    IReadOnlyList<ToolCall>? ToolCalls,         // new (ADR-010)
    string? FinishReason,                       // new (ADR-010): "stop", "tool_calls", "length"
    int PromptTokens,
    int CompletionTokens,
    long LatencyMs,
    bool WasFallback
);
```

**`StreamChunk`** gains an optional `ToolCallsDelta` for streaming tool call responses:

```csharp
public record StreamChunk(
    string RequestId,
    string Delta,
    IReadOnlyList<ToolCallDelta>? ToolCallsDelta, // new (ADR-010)
    string? FinishReason,                         // new (ADR-010)
    bool IsLast,
    int? PromptTokens,
    int? CompletionTokens,
    string Model = ""
);

public record ToolCallDelta(
    int Index,
    string? Id,
    string? Type,
    ToolCallFunctionDelta? Function
);

public record ToolCallFunctionDelta(
    string? Name,
    string? Arguments
);
```

**`OpenAiCompatibleProvider`**: `ChatCompletionRequest` is extended with `Tools`, `ToolChoice`, `TopP`, `FrequencyPenalty`, `PresencePenalty`. The response deserialization reads `choices[0].message.tool_calls` if present. SSE streaming reads `choices[0].delta.tool_calls` increments.

**`LlamaSharpProvider`**: tool calling is not supported. If `InferRequest.Tools` is non-null and non-empty, `CompleteAsync` and `CompleteStreamingAsync` throw `ProviderException(400, "tool_calling_not_supported", ...)` with `InternalErrorCategory.ModelUnavailable` — causing `ProviderOrchestrator` to skip LlamaSharp and attempt the next provider. If LlamaSharp is the only remaining option, the request fails with `InferRouterException` as normal.

**`OpenAiChatRequest`** gains `Tools`, `ToolChoice`, `TopP`, `FrequencyPenalty`, `PresencePenalty`.

**`OpenAiMessage`** is updated to support `tool_calls` and `tool_call_id` fields in the wire format.

**`ChatCompletionsEndpoint`**: maps the new fields through to `InferRequest`. Maps `InferResult.ToolCalls` and `InferResult.FinishReason` into the OpenAI response shape. Maps `StreamChunk.ToolCallsDelta` into SSE chunk responses.

### 2. `/v1/models` endpoint

A new `GET /v1/models` endpoint is added. Its behaviour depends on the `HideModels` config flag:

**`HideModels: true`** — returns a static list derived from the configured providers. Each `ProviderConfig.Model` becomes one entry. The `id` field is the model name from config; `owned_by` is the provider name.

**`HideModels: false`** — queries the first reachable provider's `GET /v1/models` endpoint and returns its response proxied directly. "First reachable" means the first provider in the `IReadOnlyList<IInferenceClient>` that does not throw when the models request is issued. If no provider responds successfully, falls back to the static behaviour.

The response follows the OpenAI `/v1/models` shape:

```json
{
  "object": "list",
  "data": [
    { "id": "llama-3.3-70b-versatile", "object": "model", "created": 0, "owned_by": "groq" }
  ]
}
```

### 3. `ProviderHealthChecker` includes FinalFallback

`ProviderHealthChecker` receives an explicit `IInferenceClient finalFallback` parameter alongside `IReadOnlyList<IInferenceClient> providers`. `CheckAllAsync` probes all cloud providers and then the final fallback, returning a combined result list. This is consistent with the pattern established by `RateLimitTracker` (issue #22) and `StatsService` (issue #25).

### 4. Extended sampling parameters passthrough

`top_p`, `frequency_penalty`, and `presence_penalty` are forwarded from the inbound OpenAI request to the upstream provider. These are added to `InferRequest`, `OpenAiChatRequest`, and `ChatCompletionRequest` in `OpenAiCompatibleProvider`. `LlamaSharpProvider` ignores them silently (they are optional and not supported by the LlamaSharp API surface in the current integration).

### 5. `system_fingerprint` in responses

`OpenAiChatResponse` gains a `system_fingerprint` field. The value is a deterministic string derived from the provider name and request ID: `"fp_{requestId[..8]}"`. This is sufficient for clients that check for its presence without comparing values across requests.

---

## What Is Not Addressed

- **Inbound API key validation** — out of scope by design
- **`n > 1`** (multiple completions per request) — low priority, not commonly used
- **`logprobs` / `top_logprobs`** — specialist use case, not addressed
- **Vision / image input** — future ADR if needed; requires `content` array support in `ChatMessage`

---

## Consequences

**Positive:**
- InferRouter is a fully functional drop-in replacement for OpenAI/OpenRouter for the vast majority of real-world client integrations
- Tool calling works transparently for cloud providers; LlamaSharp gracefully declines and allows fallback to a cloud provider
- `/v1/models` allows model discovery without manual configuration in clients like Open WebUI and LibreChat

**Negative:**
- Tool calling with LlamaSharp as the only available provider will fail — this is by design and is logged clearly
- The `HideModels: false` dynamic model list reflects only the first reachable provider's models, not a union of all providers. This is a known limitation.
- `system_fingerprint` is synthetic — it cannot be used for cross-session deduplication
