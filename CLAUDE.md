# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Status

Implementation complete. All three projects exist and build cleanly. Docker Compose stack is functional.

## What This Is

InferRouter: self-hosted, provider-agnostic LLM inference proxy in C#/.NET 10. Exposes a single OpenAI-compatible endpoint (`/v1/chat/completions` on port 5100) that routes requests through a configurable fallback chain of cloud providers, with a local LlamaSharp GGUF model as final fallback.

## Build / Test / Run

```sh
dotnet build
dotnet test
dotnet format
docker compose -f docker/docker-compose.yml up
```

## Architecture

Three projects, strict layer boundaries:

```
InferRouter.Core         # Pure C#, zero NuGet dependencies
InferRouter.Providers    # LlamaSharp + OpenAI-compatible HTTP clients
InferRouter.Api          # ASP.NET Core Minimal API host, DI composition root
```

**Core** owns all domain logic and interfaces: `ILlmProvider`, `FallbackChainExecutor`, `RateLimitTracker`, `ErrorNormalizer`, `OperationLogger`, `SecretReader`. No concrete provider knowledge here.

**Providers** implements `ILlmProvider` twice: `OpenAiCompatibleProvider` (handles Groq, Gemini, any OpenAI-compatible endpoint) and `LlamaSharpProvider` (in-process GGUF, lazy-loaded).

**Api** wires DI, reads `appsettings.json`, exposes the single endpoint.

## Key Design Rules (from ADRs)

- **Rate limits**: in-memory only, UTC midnight reset for daily limits, 60-second sliding window for RPM. No Redis, no persistence across restarts. See `docs/adr/ADR-002-in-memory-rate-limit-tracking.md`.
- **Secrets**: Docker Secrets only — path pattern `/run/secrets/{provider_name}_api_key`. Never env vars or appsettings. `SecretReader` is an injectable singleton (registered in DI, `ILogger<SecretReader>` via primary constructor) — not a static class. `OpenAiCompatibleProvider.CompleteAsync` calls `ReadApiKey` on every request; the key is never stored in a field. This means Docker Secret rotation is picked up without restart and the logger ordering problem at startup is eliminated. Missing key = `ProviderException(401)` → treated as `AuthError` → fallback to next provider. See `docs/adr/ADR-005-docker-secrets.md`.
- **Operation log**: append-only JSONL, provider name as a field (not a structural separator). One event type per line, all providers use the same schema. See `docs/adr/ADR-001-provider-agnostic-operation-log.md`.
- **Error handling**: each provider maps its HTTP status codes → `InternalErrorCategory` enum. `FallbackChainExecutor` decides retry/skip based on category, never raw status codes. See `docs/adr/ADR-006-normalized-error-handling.md`.
- **Provider chain**: config-driven via `appsettings.json`. Zero hardcoded provider lists anywhere. See `docs/adr/ADR-003-config-driven-provider-chain.md`.
- **LlamaSharp**: in-process library, not a sidecar container. Lazy-loaded to avoid memory cost when cloud providers are healthy. See `docs/adr/ADR-004-llamasharp-in-process.md`.

## Configuration Shape

`appsettings.json` drives the provider list, rate limits, and error mappings. `secrets/` directory (git-ignored) holds actual keys; `secrets.example/` (committed) holds templates.

Tested providers: Groq (30 RPM free tier), Google Gemini (10 RPM free tier), any OpenAI-compatible endpoint, LlamaSharp local GGUF.

## Reference Docs

- `docs/architecture.md` — layer diagrams, interfaces, data flow
- `docs/adr/` — ADR-001 through ADR-006, one decision per file
- `docs/soup.md` — SOUP dependency list (LlamaSharp, ASP.NET Core)
- `docs/lic-snippet.txt` - License information snippet, what needed to paste on the beggining of every source file
- `README.md` — getting started, Docker Compose usage, provider config examples
