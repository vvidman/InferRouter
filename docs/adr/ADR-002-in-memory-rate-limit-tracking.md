# ADR-002 — In-Memory Rate Limit Tracking

**Status:** Accepted  
**Date:** 2026-05-25

---

## Context

Cloud LLM providers enforce rate limits on their free tiers — typically a daily request cap (req/day) and a per-minute throughput cap (RPM). When a limit is hit, the provider returns a `429` response.

InferRouter needs to decide whether to forward a request to a provider or skip it and try the next one in the chain. There are two approaches:

1. **Reactive** — always attempt the provider, catch `429`, trigger fallback
2. **Proactive** — track usage locally, skip the provider before sending if the limit is known to be exhausted

---

## Decision

InferRouter uses **proactive in-memory tracking** as the primary mechanism, with reactive `429` handling as the safety net.

A `RateLimitTracker` maintains per-provider counters in memory:

- **Daily request counter** — incremented on each successful dispatch, reset at UTC midnight
- **RPM window counter** — sliding 60-second window, used to enforce per-minute caps before dispatch

On each routing decision:
1. If the daily counter is at or above the configured `DailyRequestLimit`, skip the provider immediately and log a `rate_limit_hit` event
2. If the RPM window is saturated, introduce a short wait or skip depending on chain depth
3. If the provider returns `429` anyway (e.g. due to clock drift or shared quota), treat it as a `rate_limit` internal category, update the local counter to the configured limit, and fall through to the next provider

**Reset schedule:** UTC midnight. A background timer fires at midnight UTC and zeroes all daily counters. RPM windows are self-expiring.

---

## Why Not Persist the Counters?

This is a single-instance, single-host deployment. The process restarts infrequently. On restart, counters reset to zero — this means a fresh restart could briefly over-dispatch until a `429` corrects the counter. This is acceptable given:

- The deployment context is a personal, low-volume tool
- The reactive safety net catches any over-dispatch
- Adding persistence (Redis, SQLite) would be disproportionate complexity

If InferRouter ever scales to multiple instances, this decision should be revisited.

---

## Consequences

**Positive:**
- Zero infrastructure dependency for quota management
- Avoids unnecessary HTTP round-trips to exhausted providers
- Fast routing decisions — counter check is O(1)

**Negative:**
- Counters are lost on restart; transient over-dispatch is possible until the first `429` corrects the state
- Does not account for quota shared across multiple InferRouter instances
