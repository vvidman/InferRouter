# ADR-003 — Config-Driven Provider Chain

**Status:** Accepted  
**Date:** 2026-05-25  
**Updated:** 2026-06-11 (ADR-009 — separate `FinalFallback` section)

---

## Context

InferRouter's core value is provider flexibility. Providers change: free tiers get adjusted, new models are released, a self-hosted Ollama instance becomes available, or a provider is simply no longer needed. If provider selection is hardcoded, every such change requires a code modification and a redeploy.

Additionally, different deployments may want different chains — one with Groq first, another with a self-hosted provider first.

---

## Decision

The provider chain is defined entirely in `appsettings.json`. No provider name, URL, model, or fallback order is hardcoded. The application reads the `Providers` array at startup, constructs the chain in the order defined, and uses it for all routing decisions.

The final fallback is configured separately in the `FinalFallback` section (see ADR-009). It is not part of the `Providers` array.

**Configuration structure:**

```json
{
  "OperationLogPath": "/var/log/inferrouter",
  "RoutingStrategy": "ChainOfResponsibility | WeightedRoundRobin | LeastUsed",
  "Providers": [
    {
      "Name": "string — used in logs and error messages",
      "Type": "openai_compatible",
      "BaseUrl": "required for openai_compatible",
      "Model": "model identifier string",
      "DailyRequestLimit": 0,
      "RequestsPerMinute": 0,
      "ErrorCodePath": "error.code",
      "ErrorMappings": []
    }
  ],
  "FinalFallback": {
    "Name": "string",
    "Type": "local_gguf | openai_compatible",
    "ModelPath": "path to .gguf file — required for local_gguf",
    "BaseUrl": "required for openai_compatible",
    "Model": "model identifier string — for openai_compatible"
  }
}
```

`RoutingStrategy` defaults to `ChainOfResponsibility` if absent or unrecognised (see ADR-007). `ErrorCodePath` defaults to `error.code` (the OpenAI error response shape) if omitted.

**Rules:**
- The `Providers` array is ordered — index 0 is the primary provider, subsequent entries are fallbacks in order
- The `Providers` array contains only `openai_compatible` entries; `local_gguf` is not a valid type here
- `BaseUrl` is required for all entries in `Providers`
- `DailyRequestLimit` and `RequestsPerMinute` of `0` mean no local limit is enforced (rely on reactive `429` handling only)
- The `FinalFallback` section is required — InferRouter will not start without it (see ADR-009)

**Startup validation:** InferRouter validates the configuration at startup and refuses to start if:
- `FinalFallback` section is absent
- A `local_gguf` entry appears in the `Providers` array (must be in `FinalFallback` instead)
- An `openai_compatible` entry in `Providers` has no `BaseUrl`
- The `FinalFallback.Type == local_gguf` and the `ModelPath` does not exist on disk
- The `FinalFallback.Type == openai_compatible` and the server is unreachable at startup

---

## Adding a New Provider

No code changes required. Add an entry to the `Providers` array in `appsettings.json` at the desired chain position and restart the container.

---

## Consequences

**Positive:**
- Provider changes require only a config edit and container restart
- The same codebase supports any OpenAI-compatible provider without modification
- Chain order is explicit and inspectable in a single file

**Negative:**
- Startup validation must be thorough — a misconfigured entry fails silently at runtime if validation is incomplete
- `appsettings.json` must not contain API keys; this is enforced by convention (Docker Secrets, see ADR-005) but not by the config schema itself
