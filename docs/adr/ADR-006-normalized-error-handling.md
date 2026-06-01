# ADR-006 — Normalized Error Handling

**Status:** Accepted  
**Date:** 2026-05-25

---

## Context

Different LLM providers return different HTTP status codes and error response shapes for semantically identical situations. For example:

- A rate limit error may be `429` on Groq, `429` on Gemini, but a `400` with a specific error code (`RESOURCE_EXHAUSTED`) on some Gemini endpoints
- An authentication failure may be `401` on most providers but `403` on others
- A model being temporarily unavailable may surface as `503`, `500`, or a `200` with an error payload depending on the provider

If the `FallbackChainExecutor` handles raw HTTP status codes, it must contain provider-specific branching logic — exactly the kind of coupling InferRouter is designed to avoid.

---

## Decision

Each provider configuration includes an `ErrorMappings` block that translates provider-specific HTTP responses to an **internal error category**. The `FallbackChainExecutor` operates exclusively on internal categories, never on raw HTTP status codes.

**Internal error categories:**

| Category | Meaning | Fallback triggered |
|---|---|---|
| `rate_limit` | Provider quota exhausted or request rate too high | Yes |
| `model_unavailable` | Requested model is temporarily unavailable | Yes |
| `server_error` | Provider-side infrastructure error | Yes, after one retry |
| `auth_error` | Invalid or missing API key | Yes — provider skipped permanently for this request (no retry) |
| `unknown_error` | No mapping matched | Yes, treated conservatively |

**Mapping resolution order:**
1. Match on `HttpStatus` + `ErrorCode` (both present in the mapping entry) — most specific
2. Match on `HttpStatus` alone
3. If no mapping matches: `unknown_error`

`ErrorCode` is matched against the provider's error response body. The field path is configurable per provider (defaults to `error.code` in the OpenAI error response shape).

**Config example:**

```json
"ErrorMappings": [
  { "HttpStatus": 429, "InternalCategory": "rate_limit" },
  { "HttpStatus": 400, "ErrorCode": "RESOURCE_EXHAUSTED", "InternalCategory": "rate_limit" },
  { "HttpStatus": 401, "InternalCategory": "auth_error" },
  { "HttpStatus": 403, "InternalCategory": "auth_error" },
  { "HttpStatus": 503, "InternalCategory": "server_error" },
  { "HttpStatus": 500, "InternalCategory": "server_error" }
]
```

---

## ErrorNormalizer

A dedicated `ErrorNormalizer` component performs the translation. It takes the raw HTTP response and the provider's `ErrorMappings` list and returns an `InternalErrorCategory`. This is the only component in the system that is aware of raw HTTP status codes in a routing context.

```csharp
public enum InternalErrorCategory
{
    RateLimit,
    ModelUnavailable,
    ServerError,
    AuthError,
    UnknownError
}
```

The `FallbackChainExecutor` calls `ErrorNormalizer.Categorize(response, providerMappings)` and branches on the returned category.

---

## Consequences

**Positive:**
- `FallbackChainExecutor` contains zero provider-specific logic
- Adding a new provider with unusual error codes requires only a config entry, not a code change
- Error handling behaviour is explicit and inspectable in `appsettings.json`
- Auth errors are distinguished from transient errors — a misconfigured key does not cause infinite fallback churn

**Negative:**
- `ErrorMappings` must be correct for each provider. An incorrect mapping (e.g. categorizing an `auth_error` as `rate_limit`) will cause silent misbehaviour. The `secrets.example/` configs and tested provider documentation serve as the reference.
- Providers that return errors in non-standard response body shapes may require an `ErrorCodePath` override in config to locate the error code field.
