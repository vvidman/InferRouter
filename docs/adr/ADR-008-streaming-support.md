# ADR-008 — Streaming Support

**Status:** Accepted
**Date:** 2026-06-09

---

## Context

InferRouter exposes an OpenAI-compatible `/v1/chat/completions` endpoint. The OpenAI API supports a `"stream": true` request parameter that switches the response from a single JSON object to a Server-Sent Events (SSE) stream of `data: {...}` chunks, terminated by `data: [DONE]`.

Many demo projects and client libraries use streaming by default. Without streaming support, InferRouter cannot be used as a drop-in replacement for OpenAI or OpenRouter in these scenarios — the client either receives an error or hangs waiting for an SSE stream that never comes.

The current implementation is fully synchronous: `ILlmProvider` exposes only `CompleteAsync`, which returns a complete `InferResult` after the entire response is generated. There is no mechanism to stream tokens as they are produced.

---

## Decision

Streaming support is added at all layers. The provider interface is extended and renamed. LlamaSharp simulates streaming because its inference pipeline generates tokens incrementally in-process but the result is buffered before returning — the simulation is transparent to callers.

### Interface rename and extension

`ILlmProvider` is renamed to `IInferenceClient`. The interface gains a `SupportsStreaming` property and a `CompleteStreamingAsync` method. The existing `CompleteAsync` is retained unchanged — the non-streaming path is not modified.

```csharp
public interface IInferenceClient
{
    string Name { get; }
    ProviderType Type { get; }
    bool SupportsStreaming { get; }

    Task<InferResult> CompleteAsync(InferRequest request, CancellationToken ct);
    IAsyncEnumerable<StreamChunk> CompleteStreamingAsync(InferRequest request, CancellationToken ct);
}
```

### New domain type: StreamChunk

```csharp
public record StreamChunk(
    string RequestId,
    string Delta,
    bool IsLast,
    int? PromptTokens,
    int? CompletionTokens
);
```

`IsLast = true` marks the final chunk. Token counts are only present on the final chunk, mirroring the OpenAI SSE protocol where usage is reported in the last non-`[DONE]` event.

### OpenAiCompatibleProvider

`SupportsStreaming = true`. `CompleteStreamingAsync` sends `"stream": true` to the upstream provider and forwards the SSE chunks directly, parsing each `data:` line and yielding a `StreamChunk`. The final `data: [DONE]` line terminates the enumerable.

### LlamaSharpProvider

`SupportsStreaming = false`. `CompleteStreamingAsync` calls `CompleteAsync` internally, then splits the completed response content into word-boundary chunks and yields them with a short delay, simulating token-by-token delivery. The caller receives a valid SSE stream and cannot distinguish this from a real streaming provider.

### ProviderOrchestrator

A new `ExecuteStreamingAsync` method is added alongside the existing `ExecuteAsync`. Provider selection uses the same fallback logic as the non-streaming path — the provider is fully selected before the first SSE chunk is written. If the selected provider does not support native streaming (`SupportsStreaming = false`), the orchestrator calls `CompleteStreamingAsync` on that provider, which handles the simulation internally.

Mid-stream failures (connection drops after the first chunk is written) are not recoverable — the client has already received a partial response. This is consistent with the behaviour of OpenAI and other upstream providers.

### ChatCompletionsEndpoint

When `stream: true` is present in the request, the endpoint sets `Content-Type: text/event-stream` and writes SSE frames as chunks arrive from `ExecuteStreamingAsync`. When `stream: false` or absent, the existing `HandleAsync` path is used unchanged.

### OperationLogger

Two new event types are added:

| Event | Description |
|---|---|
| `stream_started` | Streaming request received, provider selected |
| `stream_completed` | Final chunk sent; token counts logged here |

Token counts are only available at `stream_completed` time. No per-chunk log entries are written.

### OpenAiChatRequest

A nullable `bool? Stream` property is added. `null` and `false` both result in non-streaming behaviour.

---

## Consequences

**Positive:**
- InferRouter is a drop-in replacement for OpenAI/OpenRouter for streaming clients
- The non-streaming path is entirely unmodified — no regression risk
- LlamaSharp fallback is transparent to callers: the SSE contract is upheld regardless of which provider handles the request
- `SupportsStreaming` flag makes the capability explicit and testable

**Negative:**
- LlamaSharp streaming simulation adds latency variance: the full response must be generated before the first simulated chunk is sent. Callers receive a stream, but time-to-first-token is the same as non-streaming.
- Mid-stream failures from upstream providers cannot be recovered with a fallback — the client has already started receiving the response. This is an inherent limitation of SSE and is consistent with upstream provider behaviour.
- The interface rename from `ILlmProvider` to `IInferenceClient` is a breaking change across all usages — tests, DI registration, mocks.
