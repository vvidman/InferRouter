# ADR-004 — LlamaSharp as In-Process Library

**Status:** Accepted  
**Date:** 2026-05-25

---

## Context

The final fallback in the provider chain must work without any network connectivity — it exists precisely to handle the case where all cloud providers are unavailable or exhausted. This means it cannot be an HTTP provider.

Two implementation options exist for local GGUF model inference in a .NET environment:

1. **Sidecar container** — run a separate process (e.g. Ollama, llama.cpp server) that exposes an OpenAI-compatible HTTP endpoint, and treat it as just another `openai_compatible` provider in the chain
2. **In-process library** — use LlamaSharp to load and run the GGUF model directly inside the InferRouter process

---

## Decision

LlamaSharp is used as an **in-process library**. The local GGUF model runs inside the InferRouter process, not in a separate container.

LlamaSharp implements the same `ILlmProvider` interface as all HTTP-based providers. From the `FallbackChainExecutor`'s perspective, it is just another provider. The `local_gguf` type in config is the only indicator that this provider is handled differently at construction time.

---

## Why Not a Sidecar?

A sidecar approach would make the local fallback another `openai_compatible` entry in the chain, which is architecturally clean. However:

- It requires a second container to be running at all times, consuming memory even when not needed
- The sidecar must be healthy for the fallback to work — adding a network dependency to what is supposed to be the dependency-free last resort
- Ollama or llama.cpp server adds significant image size and startup time
- For a single-host personal deployment, the operational overhead is disproportionate

The in-process approach means the local fallback is always available as long as the InferRouter process is running and the model file exists on disk.

---

## Interface Consistency

Despite being an in-process library, LlamaSharp is surfaced behind `ILlmProvider`:

```csharp
public interface ILlmProvider
{
    string Name { get; }
    ProviderType Type { get; }
    Task<InferResult> CompleteAsync(InferRequest request, CancellationToken ct);
}
```

`ProviderType` is an enum (`OpenAiCompatible`, `LocalGguf`). The `ProviderOrchestrator` uses `Type` to identify the LlamaSharp fallback without special-casing by name.

This means:
- The `FallbackChainExecutor` contains no special-casing for local vs. remote providers
- The operation logger treats LlamaSharp calls identically to HTTP provider calls
- Swapping LlamaSharp for a different local inference library requires only a new `ILlmProvider` implementation

---

## Consequences

**Positive:**
- No second container — simpler Docker Compose setup
- Local fallback has no network dependency
- Uniform provider interface throughout the codebase

**Negative:**
- LlamaSharp loads the model into the InferRouter process's memory. On memory-constrained hosts, this may be significant. The model is loaded lazily on first use to avoid paying this cost when cloud providers are healthy.
- LlamaSharp is a .NET-specific library. If InferRouter were ever ported to another runtime, this integration point would need replacement.
