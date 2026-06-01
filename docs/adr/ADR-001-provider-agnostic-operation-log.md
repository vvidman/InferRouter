# ADR-001 — Provider-Agnostic Operation Log

**Status:** Accepted  
**Date:** 2026-05-25

---

## Context

InferRouter routes inference requests through a configurable chain of providers. Each call needs to be logged for observability: which provider handled it, whether a fallback occurred, token usage, latency, and outcome.

The naive approach is to log provider-specific fields — Groq-specific error codes, Gemini-specific quota fields, and so on. This creates a log schema that changes every time a provider is added or removed, and makes it difficult to query or analyze across providers uniformly.

---

## Decision

The operation log records **events**, not providers. The provider is an attribute on the event, not a structural element of the schema.

Every log entry has the same top-level shape regardless of which provider handled the request:

```json
{
  "ts": "ISO-8601 timestamp",
  "request_id": "uuid",
  "event": "event_type",
  "provider": "provider name from config",
  "model": "model identifier",
  "prompt_tokens": 0,
  "completion_tokens": 0,
  "latency_ms": 0,
  "fallback": false,
  "status": "ok | error"
}
```

Fallback transitions are logged as separate `infer_fallback` entries, not as a modified field on the completion entry. This gives a full audit trail of routing decisions.

**Defined event types:**

| Event | Description |
|---|---|
| `infer_started` | Request received by the router |
| `infer_ordering` | Ordered provider list resolved by the active routing strategy |
| `infer_completed` | Successful response returned to caller |
| `infer_fallback` | Provider switch triggered |
| `infer_failed` | All providers exhausted or errored |
| `rate_limit_hit` | Local quota exceeded for a provider |

---

## Format: JSONL

One JSON object per line, appended to a single file. No database, no external dependency.

Reasons:
- Readable with standard tools (`tail -f`, `jq`, `grep`)
- Zero infrastructure overhead
- Trivially importable into any analytics tool if needed later
- Append-only means no write contention issues

---

## Consequences

**Positive:**
- Log schema is stable across provider additions and removals
- Cross-provider queries work uniformly (e.g. total tokens per day, fallback rate)
- No provider-specific parsing needed at analysis time

**Negative:**
- Provider-specific metadata (e.g. Groq's queue time, Gemini's safety ratings) is not captured. If this becomes valuable, a nullable `provider_metadata` JSONB field can be added without breaking existing entries.
