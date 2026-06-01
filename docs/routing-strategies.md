# Routing Strategies

## Overview

The routing strategy layer controls how `ProviderOrchestrator` selects which provider to try for each request. The strategy operates over the configured `openai_compatible` providers; LlamaSharp is always held in reserve as the final fallback and is never part of strategy selection.

Request flow with the routing layer:

```
Incoming request
      │
      ▼
ProviderOrchestrator
      │
      ├── IRoutingStrategy.SelectNext(candidates, alreadyTried)
      │         │
      │         └── returns next provider (or null if none remain)
      │
      ├── Attempt selected provider
      │         │
      │         ├── Success → return response
      │         └── Failure → add to alreadyTried, loop back to SelectNext
      │
      └── All candidates exhausted → LlamaSharp (hard final fallback)
```

The strategy is selected once at startup based on the `RoutingStrategy` config field. All strategies share the same fallback loop — the strategy only influences *which* provider is picked at each step, not how errors are handled or logged.

---

## Configuration

The routing strategy is set via the top-level `RoutingStrategy` field in `appsettings.json`:

```json
{
  "RoutingStrategy": "ChainOfResponsibility",
  "Providers": [ ... ]
}
```

Valid values:

| Value | Description |
|---|---|
| `ChainOfResponsibility` | Try providers in config order (default) |
| `WeightedRoundRobin` | Distribute requests proportional to `DailyRequestLimit` |
| `LeastUsed` | Always route to the provider with the lowest utilisation ratio |

**Default behaviour:** if `RoutingStrategy` is absent or contains an unrecognised value, `ChainOfResponsibility` is used. No error is raised at startup.

---

## ChainOfResponsibility

Providers are tried in the order they appear in the `Providers` array. The first provider in the list receives every request unless it is rate-limited, failing, or has already been tried in the current request's fallback sequence.

This is the original InferRouter routing behaviour and the simplest to reason about. The fallback order is exactly what the operator configured.

**When to use:** when providers have a clear priority order — for example, a faster or cheaper provider should always be preferred, with others as explicit fallbacks. Also the right choice when predictability and debuggability matter more than load distribution.

**Config example:**

```json
{
  "RoutingStrategy": "ChainOfResponsibility",
  "Providers": [
    {
      "Name": "groq",
      "Type": "openai_compatible",
      "BaseUrl": "https://api.groq.com/openai/v1",
      "Model": "llama-3.3-70b-versatile",
      "DailyRequestLimit": 14400,
      "RequestsPerMinute": 30,
      "ErrorMappings": [
        { "HttpStatus": 429, "InternalCategory": "RateLimit" },
        { "HttpStatus": 401, "InternalCategory": "AuthError" }
      ]
    },
    {
      "Name": "gemini",
      "Type": "openai_compatible",
      "BaseUrl": "https://generativelanguage.googleapis.com/v1beta/openai",
      "Model": "gemini-2.5-flash",
      "DailyRequestLimit": 1500,
      "RequestsPerMinute": 10,
      "ErrorMappings": [
        { "HttpStatus": 429, "InternalCategory": "RateLimit" },
        { "HttpStatus": 401, "InternalCategory": "AuthError" }
      ]
    },
    {
      "Name": "llamasharp",
      "Type": "local_gguf",
      "ModelPath": "/models/llama.gguf"
    }
  ]
}
```

In this configuration `groq` handles every request. `gemini` is only reached if Groq is rate-limited or failing. LlamaSharp is the final fallback.

---

## WeightedRoundRobin

Requests are distributed across providers in proportion to their `DailyRequestLimit`. A provider with a higher daily quota receives proportionally more requests over time.

**Weight calculation:** each provider's weight is its `DailyRequestLimit`. The probability of a provider being selected on a given request is `DailyRequestLimit / sum(DailyRequestLimit for all eligible providers)`.

**Provider exclusion:** providers with `DailyRequestLimit: 0` are excluded from weighted selection. A value of `0` means "no local limit enforced" and provides no quota information to derive a weight from. Excluded providers are still available as fallbacks if all weighted providers fail.

**When to use:** when multiple providers are at comparable quality and you want to maximise total throughput across their quotas. Spreads wear across providers so no single one hits its daily cap early.

**Config example** (Groq at 14 400 req/day and Gemini at 1 500 req/day — Groq receives ~90.6% of traffic, Gemini ~9.4%):

```json
{
  "RoutingStrategy": "WeightedRoundRobin",
  "Providers": [
    {
      "Name": "groq",
      "Type": "openai_compatible",
      "BaseUrl": "https://api.groq.com/openai/v1",
      "Model": "llama-3.3-70b-versatile",
      "DailyRequestLimit": 14400,
      "RequestsPerMinute": 30,
      "ErrorMappings": [
        { "HttpStatus": 429, "InternalCategory": "RateLimit" },
        { "HttpStatus": 401, "InternalCategory": "AuthError" }
      ]
    },
    {
      "Name": "gemini",
      "Type": "openai_compatible",
      "BaseUrl": "https://generativelanguage.googleapis.com/v1beta/openai",
      "Model": "gemini-2.5-flash",
      "DailyRequestLimit": 1500,
      "RequestsPerMinute": 10,
      "ErrorMappings": [
        { "HttpStatus": 429, "InternalCategory": "RateLimit" },
        { "HttpStatus": 401, "InternalCategory": "AuthError" }
      ]
    },
    {
      "Name": "llamasharp",
      "Type": "local_gguf",
      "ModelPath": "/models/llama.gguf"
    }
  ]
}
```

---

## LeastUsed

At each routing decision, the provider with the lowest utilisation ratio is selected. Utilisation is calculated as `DailyCount / DailyLimit`.

**Ratio calculation:** normalising by the daily limit makes providers with different quota sizes comparable. A provider that has used 500 of 14 400 requests (3.5%) is considered less used than one that has used 50 of 500 requests (10%).

**Tiebreaker:** when two or more providers have the same utilisation ratio (including at the start of the day when all counters are zero), selection is random among tied providers. This distributes load without imposing a hidden ordering.

**Provider exclusion:** providers with `DailyRequestLimit: 0` are excluded — the ratio is undefined without a denominator. They remain available as fallbacks if all eligible providers fail.

**When to use:** when avoiding premature exhaustion of any single provider's daily quota is the primary concern. Keeps all providers running into the evening rather than depleting the highest-weighted provider first.

**Config example:**

```json
{
  "RoutingStrategy": "LeastUsed",
  "Providers": [
    {
      "Name": "groq",
      "Type": "openai_compatible",
      "BaseUrl": "https://api.groq.com/openai/v1",
      "Model": "llama-3.3-70b-versatile",
      "DailyRequestLimit": 14400,
      "RequestsPerMinute": 30,
      "ErrorMappings": [
        { "HttpStatus": 429, "InternalCategory": "RateLimit" },
        { "HttpStatus": 401, "InternalCategory": "AuthError" }
      ]
    },
    {
      "Name": "gemini",
      "Type": "openai_compatible",
      "BaseUrl": "https://generativelanguage.googleapis.com/v1beta/openai",
      "Model": "gemini-2.5-flash",
      "DailyRequestLimit": 1500,
      "RequestsPerMinute": 10,
      "ErrorMappings": [
        { "HttpStatus": 429, "InternalCategory": "RateLimit" },
        { "HttpStatus": 401, "InternalCategory": "AuthError" }
      ]
    },
    {
      "Name": "llamasharp",
      "Type": "local_gguf",
      "ModelPath": "/models/llama.gguf"
    }
  ]
}
```

At the start of the day, both providers are at 0% utilisation; traffic is split randomly. As the day progresses, whichever provider's ratio climbs faster will be deprioritised, keeping both providers at roughly equal utilisation percentages.

---

## Local Fallback

LlamaSharp (`local_gguf`) is not part of strategy selection in any strategy. It is always invoked as a hard final fallback after all `openai_compatible` providers in the candidate list have been tried and exhausted.

LlamaSharp is triggered when:
- All cloud providers are rate-limited (daily or per-minute limits exhausted locally)
- All cloud providers have returned errors that trigger fallback (`RateLimit`, `ModelUnavailable`, `ServerError`)
- A cloud provider is unreachable (network error)

LlamaSharp is **not** triggered when:
- A cloud provider returns `AuthError` — this is logged as a fatal configuration error and does not fall through to LlamaSharp
- The request itself is malformed

LlamaSharp has no rate limit, no API key, and no network dependency. It is always available as long as the model file exists at the configured `ModelPath`. The model is loaded lazily on first use to avoid memory overhead when cloud providers are healthy.

---

## Choosing a Strategy

| Scenario | Recommended strategy |
|---|---|
| Providers have a clear priority order (best provider first) | `ChainOfResponsibility` |
| Multiple providers at similar quality; maximise total throughput | `WeightedRoundRobin` |
| Avoid hitting any provider's daily cap early in the day | `LeastUsed` |
| Single provider + local fallback only | `ChainOfResponsibility` |
| Unsure / first deployment | `ChainOfResponsibility` (default) |

`ChainOfResponsibility` is the right starting point for most deployments. Switch to `WeightedRoundRobin` or `LeastUsed` when you have observed quota exhaustion on a single provider and want proactive distribution rather than reactive fallback.
