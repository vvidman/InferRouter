# ADR-004 — LlamaSharp as In-Process Library

**Status:** Accepted  
**Date:** 2026-05-25  
**Updated:** 2026-06-11 (ADR-009 — LlamaSharp is now one of two valid `FinalFallback` types)

---

## Context

The final fallback provider must work without any network connectivity — it exists precisely to handle the case where all cloud providers are unavailable or exhausted. This means it cannot be an HTTP provider.

Two implementation options exist for local GGUF model inference in a .NET environment:

1. **Sidecar container** — run a separate process (e.g. Ollama, llama.cpp server) that exposes an OpenAI-compatible HTTP endpoint, and treat it as just another `openai_compatible` provider in the chain
2. **In-process library** — use LlamaSharp to load and run the GGUF model directly inside the InferRouter process

---

## Decision

LlamaSharp is used as an **in-process library** for the `local_gguf` final fallback type. The local GGUF model runs inside the InferRouter process, not in a separate container.

As of ADR-009, the `FinalFallback` section supports two types:
- `local_gguf` — LlamaSharp in-process (this ADR)
- `openai_compatible` — any OpenAI-compatible HTTP server (e.g. Ollama), configured with a `BaseUrl`

Operators who prefer managing a separate Ollama server over GGUF files and LlamaSharp's native dependencies can use `openai_compatible` as their `FinalFallback` type instead. Both types implement `IInferenceClient` identically from `ProviderOrchestrator`'s perspective.

LlamaSharp implements the same `IInferenceClient` interface as all HTTP-based providers. The `local_gguf` type in config is the only indicator that this provider is handled differently at construction time.

---

## Why Not a Sidecar (for `local_gguf`)?

A sidecar approach would make the local fallback another `openai_compatible` entry, which is architecturally clean. However:

- It requires a second container to be running at all times, consuming memory even when not needed
- The sidecar must be healthy for the fallback to work — adding a network dependency to what is supposed to be the dependency-free last resort
- Ollama or llama.cpp server adds significant image size and startup time
- For a single-host personal deployment, the operational overhead is disproportionate

The in-process approach means the `local_gguf` fallback is always available as long as the InferRouter process is running and the model file exists on disk.

---

## Interface Consistency

Despite being an in-process library, LlamaSharp is surfaced behind `IInferenceClient`:

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

`ProviderType` is an enum (`OpenAiCompatible`, `LocalGguf`). `ProviderOrchestrator` receives the final fallback as an explicit `IInferenceClient` dependency — it never inspects `ProviderType` to identify it.

This means:
- `ProviderOrchestrator` contains no special-casing for local vs. remote final fallback
- The operation logger treats LlamaSharp calls identically to HTTP provider calls
- Swapping LlamaSharp for a different local inference library requires only a new `IInferenceClient` implementation

---

## Consequences

**Positive:**
- No second container required for the `local_gguf` final fallback — simpler Docker Compose setup
- `local_gguf` fallback has no network dependency
- Uniform provider interface throughout the codebase
- Operators can choose between `local_gguf` (LlamaSharp) and `openai_compatible` (Ollama) as their `FinalFallback` type

**Negative:**
- LlamaSharp loads the model into the InferRouter process's memory. On memory-constrained hosts, this may be significant. The model is loaded lazily on first use to avoid paying this cost when cloud providers are healthy.
- LlamaSharp is a .NET-specific library. If InferRouter were ever ported to another runtime, this integration point would need replacement.
