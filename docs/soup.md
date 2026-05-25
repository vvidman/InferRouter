# SOUP — Software of Unknown Provenance

All third-party components used in InferRouter. Versions should be pinned in the project file and reviewed on each update.

---

## Runtime Dependencies

### LlamaSharp
- **Source:** NuGet — `LLamaSharp`
- **Version:** latest stable at project init (pin in `.csproj`)
- **Purpose:** In-process GGUF model inference. Used exclusively in `InferRouter.Providers` by `LlamaSharpProvider`.
- **License:** MIT
- **Notes:** Requires a platform-specific backend package. The appropriate backend must be selected based on the host hardware:

| Package | Use case |
|---|---|
| `LLamaSharp.Backend.Cpu` | CPU-only — no GPU, cross-platform |
| `LLamaSharp.Backend.Cuda11` | NVIDIA GPU, CUDA 11.x |
| `LLamaSharp.Backend.Cuda12` | NVIDIA GPU, CUDA 12.x |
| `LLamaSharp.Backend.MacMetal` | Apple Silicon / Metal |

For the default Docker deployment (CPU-only), `LLamaSharp.Backend.Cpu` is used.

---

## Framework / Platform

### .NET 10 SDK
- **Source:** Microsoft — https://dotnet.microsoft.com
- **Purpose:** Runtime and base class libraries. Provides `HttpClient`, `System.Text.Json`, `Microsoft.Extensions.Configuration`, `Microsoft.Extensions.DependencyInjection`, and `Microsoft.Extensions.Hosting`.
- **License:** MIT
- **Notes:** No additional NuGet packages are required for HTTP client, JSON serialization, configuration binding, or DI — all are included in the .NET 10 SDK.

### ASP.NET Core (included in .NET 10)
- **Source:** Microsoft — part of .NET 10 SDK
- **Purpose:** Minimal API host for the `/v1/chat/completions` endpoint.
- **License:** MIT
- **Notes:** No separate NuGet install required.

---

## Infrastructure

### Docker Engine
- **Source:** Docker Inc. — https://www.docker.com
- **Version:** 24+ recommended
- **Purpose:** Container runtime for the InferRouter service.
- **License:** Apache 2.0 (Docker Engine open source components)
- **Notes:** Docker Secrets support requires Swarm mode OR `docker-compose` file-based secrets (the latter is used here — no Swarm required).

### Docker Compose
- **Source:** Docker Inc. — bundled with Docker Desktop or installable separately
- **Version:** Compose V2 (plugin-based, `docker compose`)
- **Purpose:** Multi-container orchestration, secret mounting, port mapping.
- **License:** Apache 2.0
- **Notes:** V1 (`docker-compose` binary) is deprecated. V2 (`docker compose` plugin) is required.

---

## GGUF Model File

The local fallback model is a GGUF-format file loaded by LlamaSharp. This is not a software package but is a required runtime artifact.

- **Format:** GGUF (llama.cpp compatible)
- **Source:** User-provided — not bundled in the repository or Docker image
- **Recommended source:** Hugging Face model hub (https://huggingface.co/models?search=gguf)
- **Suggested baseline:** A quantized Llama 3 or Mistral model at Q4_K_M quantization for CPU inference
- **Notes:** The model file path is configured via `appsettings.json` (`Providers[].ModelPath`) and must be mounted into the container as a volume. It is **not** a Docker Secret — it is a large binary file, not a credential.

```yaml
# docker-compose.yml — model volume mount example
services:
  inferrouter:
    volumes:
      - /path/to/models:/models:ro
```

---

## Summary Table

| Component | Type | NuGet / External | License |
|---|---|---|---|
| LlamaSharp | NuGet | `LLamaSharp` | MIT |
| LlamaSharp CPU backend | NuGet | `LLamaSharp.Backend.Cpu` | MIT |
| .NET 10 SDK | Platform | — | MIT |
| ASP.NET Core | Platform | included in .NET 10 | MIT |
| Docker Engine | Infrastructure | — | Apache 2.0 |
| Docker Compose V2 | Infrastructure | — | Apache 2.0 |
| GGUF model file | Runtime artifact | user-provided | varies per model |

---

## Intentional Omissions

The following were considered and deliberately excluded to minimise the dependency surface:

| Omitted | Reason |
|---|---|
| Entity Framework Core | No database; JSONL log needs no ORM |
| Serilog / NLog | `OperationLogger` writes structured JSONL directly; framework logging (`ILogger`) used for diagnostics only |
| Polly | Retry logic is simple (one retry on `server_error`) and implemented inline in `FallbackChainExecutor` |
| MediatR | No CQRS pattern warranted for a single-endpoint proxy |
| OpenTelemetry SDK | Out of scope; JSONL log is a potential future import source for an external observability tool |
| Redis | Rate limit counters are in-memory; single-instance deployment makes distributed state unnecessary |
