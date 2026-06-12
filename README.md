# InferRouter

Self-hosted, provider-agnostic LLM inference proxy in C#/.NET 10. Exposes an OpenAI-compatible API that routes requests through a configurable fallback chain of cloud providers, with a local GGUF model or Ollama server as final fallback.

Drop-in replacement for OpenAI or OpenRouter: change the base URL and API key, nothing else.

---

## What It Does

- Receives OpenAI-compatible requests (`/v1/chat/completions`, `/v1/models`)
- Routes through a configurable provider chain (Groq → Gemini → ... → final fallback)
- Falls back automatically on rate limits, errors, or unavailability
- Streams responses via SSE when `"stream": true` is set
- Passes through tool calling / function calling transparently to cloud providers
- Runs entirely on your own infrastructure — no external dependencies beyond the providers you configure

---

## Endpoints

| Method | Path | Description |
|---|---|---|
| `POST` | `/v1/chat/completions` | OpenAI-compatible chat completion (streaming + non-streaming) |
| `GET` | `/v1/models` | Model list (static from config or proxied from first available provider) |
| `GET` | `/health` | Service liveness check |
| `GET` | `/health/providers` | Per-provider health status via live probe |
| `GET` | `/stats/live` | Current rate limit counters for all providers |
| `GET` | `/stats/history` | Historical JSONL operation log for a given date |

---

## Supported OpenAI API Features

| Feature | Status |
|---|---|
| Chat completions | ✅ |
| Streaming (SSE) | ✅ |
| Tool calling / function calling | ✅ (cloud providers); ❌ LlamaSharp (falls back to cloud) |
| `temperature`, `max_tokens` | ✅ |
| `top_p`, `frequency_penalty`, `presence_penalty` | ✅ |
| `system_fingerprint` in response | ✅ |
| `finish_reason` in response | ✅ |
| Model discovery (`/v1/models`) | ✅ |
| Multiple completions (`n > 1`) | ❌ |
| Log probabilities (`logprobs`) | ❌ |
| Vision / image input | ❌ |
| Inbound API key validation | ❌ (by design — self-hosted, network-isolated) |

---

## Getting Started

```bash
# 1. Copy secret placeholders and fill in your API keys
cp -r secrets.example secrets
echo "your-groq-api-key"   > secrets/groq_api_key.txt
echo "your-gemini-api-key" > secrets/gemini_api_key.txt

# 2. Place a GGUF model file (if using local_gguf final fallback)
#    Default path: models/model.gguf
#    Download from https://huggingface.co/models?search=gguf

# 3. Build and start
docker compose -f docker/docker-compose.yml up --build
```

The service listens on port `5100`.

---

## Configuration

All configuration lives in `appsettings.json` under the `InferRouter` key.

```json
{
  "InferRouter": {
    "OperationLogPath": "/var/log/inferrouter",
    "RoutingStrategy": "ChainOfResponsibility",
    "HideModels": false,
    "Providers": [
      {
        "Name": "groq",
        "Type": "openai_compatible",
        "BaseUrl": "https://api.groq.com/openai/v1",
        "Model": "llama-3.3-70b-versatile",
        "DailyRequestLimit": 14400,
        "RequestsPerMinute": 30,
        "ErrorCodePath": "error.code",
        "ErrorMappings": [
          { "HttpStatus": 429, "InternalCategory": "RateLimit" },
          { "HttpStatus": 401, "InternalCategory": "AuthError" },
          { "HttpStatus": 503, "InternalCategory": "ServerError" }
        ]
      },
      {
        "Name": "gemini",
        "Type": "openai_compatible",
        "BaseUrl": "https://generativelanguage.googleapis.com/v1beta/openai",
        "Model": "gemini-2.0-flash",
        "DailyRequestLimit": 1500,
        "RequestsPerMinute": 10,
        "ErrorCodePath": "error.status",
        "ErrorMappings": [
          { "HttpStatus": 429, "InternalCategory": "RateLimit" },
          { "HttpStatus": 401, "InternalCategory": "AuthError" }
        ]
      }
    ],
    "FinalFallback": {
      "Name": "local",
      "Type": "local_gguf",
      "ModelPath": "/models/model.gguf"
    }
  }
}
```

### Routing Strategies

| Value | Behaviour |
|---|---|
| `ChainOfResponsibility` | Try providers in config order (default) |
| `WeightedRoundRobin` | Distribute requests proportional to `DailyRequestLimit` |
| `LeastUsed` | Always route to the provider with the lowest utilisation ratio |

### HideModels

Controls `GET /v1/models` behaviour:

- `false` (default) — proxies the model list from the first reachable cloud provider
- `true` — returns a static list built from the configured providers' `Model` fields

### FinalFallback

Two types are supported:

**Option 1 — LlamaSharp (in-process GGUF, no network dependency):**
```json
"FinalFallback": {
  "Name": "local",
  "Type": "local_gguf",
  "ModelPath": "/models/model.gguf"
}
```

**Option 2 — Ollama or any OpenAI-compatible local server:**
```json
"FinalFallback": {
  "Name": "ollama",
  "Type": "openai_compatible",
  "BaseUrl": "http://localhost:11434/v1",
  "Model": "llama3.2"
}
```

When `Type: openai_compatible` is used, InferRouter checks reachability at startup and will refuse to start if the server is not responding. The Ollama server must be running before InferRouter starts.

### Secrets

API keys are managed via Docker Secrets. Place key files in the `secrets/` directory:

```
secrets/
  groq_api_key.txt
  gemini_api_key.txt
```

Key files are mounted into the container at `/run/secrets/{provider_name}_api_key`. Keys are read fresh on every request — Docker Secret rotation is picked up without a restart.

---

## Testing Against InferRouter

Any project built against the OpenAI API can be pointed at InferRouter by changing two things:

```python
# Python (openai SDK)
client = openai.OpenAI(
    base_url="http://192.168.0.69:5100/v1",
    api_key="unused"   # InferRouter does not validate inbound keys
)
```

```javascript
// JavaScript (openai SDK)
const client = new OpenAI({
  baseURL: "http://192.168.0.69:5100/v1",
  apiKey: "unused"
});
```

Tool calling, streaming, and sampling parameters all work as expected with cloud providers.

---

## Tech Stack

| Layer | Technology |
|---|---|
| Runtime | .NET 10, C# |
| HTTP layer | ASP.NET Core Minimal API |
| Local inference | LlamaSharp 0.20.0 (GGUF via llama.cpp) |
| Operation log | Append-only JSONL, daily rotation |
| Containers | Docker Compose |
| Secret management | Docker Secrets |

---

## Project Structure

```
InferRouter/
├── src/
│   ├── InferRouter.Core/            ← interfaces, domain models, zero external deps
│   ├── InferRouter.Providers/       ← IInferenceClient implementations
│   └── InferRouter.Api/             ← ASP.NET Core host, endpoints, DI composition
├── docker/
│   └── docker-compose.yml
├── secrets.example/
├── docs/
│   ├── architecture.md
│   ├── routing-strategies.md
│   └── adr/                         ← ADR-001 through ADR-010
├── README.md
└── InferRouter.sln
```

---

## Reference Docs

- `docs/architecture.md` — layer diagrams, interfaces, data flow
- `docs/routing-strategies.md` — routing strategy details and when to use each
- `docs/adr/` — one ADR per architectural decision (ADR-001 through ADR-010)
- `docs/soup.md` — SOUP dependency list
