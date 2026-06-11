# ADR-009 — Flexible Final Fallback

**Status:** Accepted
**Date:** 2026-06-11

---

## Context

ADR-004 established LlamaSharp as the mandatory final fallback provider, running in-process as a .NET library. This was a deliberate decision for the initial deployment context: a single homelab host where the model file is always present on disk and no network dependency is acceptable for the last-resort fallback.

Two constraints have since emerged:

1. **Developer experience**: operators who are not focused on local model management — for example, those building and testing integrations — may prefer to run an Ollama server as their local fallback rather than managing GGUF files and LlamaSharp's native dependencies. Ollama is easier to set up and update, and exposes an OpenAI-compatible API.

2. **Architectural rigidity**: the final fallback is currently hardcoded as `Type: LocalGguf` in config validation (Program.cs, validation 4 and 5) and in `ProviderOrchestrator`, which explicitly identifies the LlamaSharp provider by `ProviderType.LocalGguf`. The concept of "final fallback" is implicit — it is simply the last entry in the array, enforced by type.

The current design cannot express "my final fallback is an Ollama server" without misusing the `OpenAiCompatible` provider type in a position that has special semantics.

---

## Decision

The final fallback is promoted to an **explicit, named configuration section** — separate from the `Providers` array. It can be either a `local_gguf` (LlamaSharp, in-process) or an `openai_compatible` (any OpenAI-compatible HTTP server, e.g. Ollama).

### Config structure change

The `FinalFallback` section is extracted from `Providers` into its own top-level key:

```json
{
  "InferRouter": {
    "OperationLogPath": "/var/log/inferrouter",
    "RoutingStrategy": "ChainOfResponsibility",
    "Providers": [
      { "Name": "groq",   "Type": "openai_compatible", ... },
      { "Name": "gemini", "Type": "openai_compatible", ... }
    ],
    "FinalFallback": {
      "Name": "local-llama",
      "Type": "local_gguf",
      "ModelPath": "/opt/inferrouter/models/model.gguf"
    }
  }
}
```

Or with Ollama as the final fallback:

```json
"FinalFallback": {
  "Name": "ollama",
  "Type": "openai_compatible",
  "BaseUrl": "http://192.168.0.69:11434/v1",
  "Model": "llama3.2"
}
```

The `Providers` array contains only cloud/HTTP providers. `LocalGguf` is no longer a valid type inside `Providers`. The `FinalFallback` section is required — InferRouter will not start without it.

### Startup validation changes

The following existing validations are removed or replaced:

- **Removed**: "last provider must be `LocalGguf`" (validation 4 in Program.cs)
- **Removed**: "exactly one `LocalGguf` entry" (validation 5 in Program.cs)
- **Added**: `FinalFallback` section must be present
- **Added**: `FinalFallback.Type` must be `local_gguf` or `openai_compatible`
- **Added**: if `FinalFallback.Type == local_gguf`: `ModelPath` must exist on disk (existing check, moved)
- **Added**: if `FinalFallback.Type == openai_compatible`: HTTP health check against `BaseUrl` at startup; hard failure if unreachable

### Health check for `openai_compatible` final fallback

At startup, if `FinalFallback.Type == openai_compatible`, InferRouter sends a `GET {BaseUrl}` request with a 5-second timeout. If the connection is refused or the request times out, InferRouter logs a fatal error and exits.

The check verifies only that the server is reachable — HTTP response status is not evaluated. Model availability and correctness of the configured model name are the operator's responsibility; a misconfigured model will surface as an error on the first actual inference request.

This check is **InferRouter's responsibility only** — it has no knowledge of how or where the target server is deployed. If the server is not running when InferRouter starts, the operator must start it first. There is no dependency encoding between containers.

### Orchestrator changes

`ProviderOrchestrator` currently identifies the final fallback by iterating `IReadOnlyList<IInferenceClient>` and finding the entry with `ProviderType.LocalGguf`. This implicit identification is replaced with an explicit `IInferenceClient FinalFallback` dependency injected separately from the provider list.

`ProviderOrchestrator` receives:
- `IReadOnlyList<IInferenceClient> providers` — cloud/HTTP providers only, in routing strategy order
- `IInferenceClient finalFallback` — the final fallback, regardless of type

`IRoutingStrategy.GetOrderedProviders()` never includes the final fallback. The orchestrator appends it manually as the last resort, exactly as today — the only change is that it is typed as `IInferenceClient` rather than identified by `ProviderType`.

### `ProviderType` enum

`LocalGguf` is retained in the enum — it is still a valid provider type for the `FinalFallback` section and is used internally by `LlamaSharpProvider`. It is simply no longer valid inside the `Providers` array.

---

## Consequences

**Positive:**
- Operators can use Ollama (or any OpenAI-compatible local server) as the final fallback without any code changes
- The concept of "final fallback" is explicit in configuration — no longer an implicit positional convention
- `ProviderOrchestrator` no longer contains type-based special-casing for the fallback provider
- `IRoutingStrategy` implementations are simpler — they only see cloud providers, never the fallback

**Negative:**
- Breaking config change: existing `appsettings.json` files must be updated. The `LocalGguf` entry must be moved from `Providers` to `FinalFallback`. This is a one-time migration.
- `openai_compatible` final fallback adds a startup network dependency. If the target server is not running when InferRouter starts, the startup fails. This is intentional and consistent with the `local_gguf` behaviour (model file must exist on disk).
- The startup health check verifies only reachability, not model availability. A misconfigured model name will not be caught at startup — it will surface as an error on the first inference request.
