# ADR-003 — Config-Driven Provider Chain

**Status:** Accepted  
**Date:** 2026-05-25

---

## Context

InferRouter's core value is provider flexibility. Providers change: free tiers get adjusted, new models are released, a self-hosted Ollama instance becomes available, or a provider is simply no longer needed. If provider selection is hardcoded, every such change requires a code modification and a redeploy.

Additionally, different deployments may want different chains — one with Groq first, another with a self-hosted provider first.

---

## Decision

The provider chain is defined entirely in `appsettings.json`. No provider name, URL, model, or fallback order is hardcoded. The application reads the `Providers` array at startup, constructs the chain in the order defined, and uses it for all routing decisions.

**Configuration structure:**

```json
{
  "Providers": [
    {
      "Name": "string — used in logs and error messages",
      "Type": "openai_compatible | local_gguf",
      "BaseUrl": "required for openai_compatible, omitted for local_gguf",
      "Model": "model identifier string",
      "DailyRequestLimit": 0,
      "RequestsPerMinute": 0,
      "ErrorMappings": []
    }
  ]
}
```

**Rules:**
- The array is ordered — index 0 is the primary provider, subsequent entries are fallbacks in order
- Exactly one entry with `"Type": "local_gguf"` must be present, and it must be last
- `BaseUrl` is required for `openai_compatible` and must be omitted for `local_gguf`
- `DailyRequestLimit` and `RequestsPerMinute` of `0` mean no local limit is enforced (rely on reactive `429` handling only)

**Startup validation:** InferRouter validates the provider list at startup and refuses to start if:
- No `local_gguf` entry is present
- A `local_gguf` entry is not last
- An `openai_compatible` entry has no `BaseUrl`
- The `ModelPath` for a `local_gguf` entry does not exist on disk

---

## Adding a New Provider

No code changes required. Add an entry to `appsettings.json` at the desired chain position and restart the container.

---

## Consequences

**Positive:**
- Provider changes require only a config edit and container restart
- The same codebase supports any OpenAI-compatible provider without modification
- Chain order is explicit and inspectable in a single file

**Negative:**
- Startup validation must be thorough — a misconfigured entry fails silently at runtime if validation is incomplete
- `appsettings.json` must not contain API keys; this is enforced by convention (Docker Secrets, see ADR-005) but not by the config schema itself
