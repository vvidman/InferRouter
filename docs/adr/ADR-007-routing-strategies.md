# ADR-007 — Routing Strategies

**Status:** Accepted  
**Date:** 2026-06-01

---

## Context

The initial `FallbackChainExecutor` implements a single routing behaviour: try providers in the configured order, skipping any that are rate-limited or failing, until one succeeds or the chain is exhausted. This is the right default for most deployments, but it is the only option.

As InferRouter matures, operators need more control over how requests are distributed across providers:

- A deployment with multiple providers at similar quality tiers might want **load distribution** across them rather than always hammering the first provider until it fails
- A deployment tracking quota spend might want **utilisation-aware routing** that proactively routes away from providers running low on their daily allocation
- A deployment that wants predictable, simple fallback behaviour should retain access to the original sequential approach

Embedding all three behaviours in a single class via flags or conditionals would make `FallbackChainExecutor` increasingly complex and difficult to test. A strategy abstraction cleanly separates the routing policy from the fallback execution loop.

---

## Decision

### Refactor `FallbackChainExecutor` into `ProviderOrchestrator` with a pluggable `IRoutingStrategy`

`FallbackChainExecutor` is refactored into `ProviderOrchestrator`. The class retains ownership of the fallback execution loop — retrying on transient errors, logging events, tracking rate limits — but delegates the question of *which provider to try next* to an injected `IRoutingStrategy`.

```csharp
public interface IRoutingStrategy
{
    ILlmProvider? SelectNext(IReadOnlyList<ILlmProvider> candidates, IReadOnlyList<ILlmProvider> alreadyTried);
}
```

`ProviderOrchestrator` calls `SelectNext` at each routing step, passing the full eligible candidate list and the providers already attempted in this request. The strategy returns the next provider to try, or `null` if no suitable candidate remains. This interface is sufficient for all three initial strategies and keeps the contract minimal.

### Strategy set: `ChainOfResponsibility`, `WeightedRoundRobin`, `LeastUsed`

Three strategies cover the scenarios identified:

**`ChainOfResponsibility`** — preserves the original behaviour. Providers are tried in the order they appear in `appsettings.json`. This is the simplest strategy and the most predictable for debugging. Operators who have explicitly ordered their providers by preference should use this.

**`WeightedRoundRobin`** — distributes requests across providers in proportion to their configured `DailyRequestLimit`. Providers with higher quotas receive proportionally more requests. Operators with multiple providers at comparable quality who want to spread load and maximise total throughput across free-tier quotas should use this.

**`LeastUsed`** — at each routing decision, selects the provider with the lowest utilisation ratio (`DailyCount / DailyLimit`). Operators who want to proactively balance quota consumption to avoid hitting a single provider's daily cap early in the day should use this.

These three strategies together cover the full space of common routing policies without requiring custom code.

### `IRateLimitTracker` interface introduction

`RateLimitTracker` is currently a concrete class. `WeightedRoundRobin` and `LeastUsed` both need read access to per-provider usage data (daily counts and limits) at selection time. Rather than coupling strategies directly to the concrete class, the read-relevant members are extracted into `IRateLimitTracker`. This enables unit testing of strategies with a mock tracker and aligns with the DI consistency pattern already established in the codebase (`ILlmProvider`, `IOperationLogger`, etc.).

### LlamaSharp remains a hard final fallback in all strategies

LlamaSharp (`local_gguf`) is excluded from strategy selection entirely. All three strategies operate only over the `openai_compatible` providers. If all selected providers fail, `ProviderOrchestrator` falls through to LlamaSharp unconditionally, regardless of which strategy is active.

This is intentional:

- LlamaSharp's role is to guarantee a response when the network is unavailable or all cloud quotas are exhausted. Including it in rotation would defeat this purpose — it could be selected first, bypassing cloud providers unnecessarily.
- Weighted selection using `DailyRequestLimit` is not meaningful for LlamaSharp, which has no rate limit concept.
- The `LeastUsed` ratio calculation is undefined for a provider with no daily limit.

Operators who want to use LlamaSharp as a peer provider rather than a fallback should configure it as an `openai_compatible` entry pointing to a local llama.cpp server (see ADR-004).

### `WeightedRoundRobin` derives weights from `DailyRequestLimit`

An explicit `Weight` field in provider config was considered and rejected. Deriving weights automatically from `DailyRequestLimit` avoids a redundant configuration field that must be kept in sync with quota reality. The `DailyRequestLimit` already encodes the operator's understanding of how much traffic a provider can sustain — it is the correct source of truth for proportional distribution.

Consequence: a provider with `DailyRequestLimit: 0` is excluded from weighted selection entirely. A value of `0` is the existing convention for "no local limit enforced" (see ADR-003), meaning there is no quota information available to derive a weight from. Such providers are skipped by `WeightedRoundRobin`; operators who want them included must set a non-zero `DailyRequestLimit`.

### `LeastUsed` uses a utilisation ratio, not an absolute counter

Comparing raw daily request counts across providers with different `DailyRequestLimit` values is not meaningful — a provider with a 14 400 req/day quota and 1 000 requests used is far less constrained than a provider with a 1 500 req/day quota and 1 000 requests used. The ratio `DailyCount / DailyLimit` normalises across quota sizes and correctly reflects relative availability.

**Tiebreaker is random.** When two or more providers have the same utilisation ratio (including the common case at the start of a day when all counters are zero), random selection distributes load across them without imposing a hidden ordering. A deterministic tiebreaker (e.g. alphabetical by name, or index order) would reduce to chain-of-responsibility behaviour for the all-zero case, defeating the purpose of `LeastUsed`.

Providers with `DailyRequestLimit: 0` are excluded from `LeastUsed` for the same reason as `WeightedRoundRobin`: the ratio is undefined.

### Unknown or missing `RoutingStrategy` silently defaults to `ChainOfResponsibility`

At startup, if the `RoutingStrategy` config key is absent or contains an unrecognised value, the system silently selects `ChainOfResponsibility`. This matches the principle of least surprise: the existing behaviour is preserved without requiring operators to add a new config field. A startup error on an unknown strategy value would break deployments during upgrades before operators have had a chance to read the release notes.

The tradeoff is that a typo in the strategy name silently uses the default rather than surfacing a validation error. This is acceptable given the low frequency of strategy changes in practice and the visibility of the routing behaviour in operation logs.

---

## Consequences

**Positive:**
- Routing policy is fully decoupled from execution loop and error handling
- Each strategy is independently testable via `IRateLimitTracker` mock
- New strategies can be added without modifying `ProviderOrchestrator`
- Operators can change routing behaviour with a single config field, no code change or restart beyond config reload

**Negative:**
- `WeightedRoundRobin` and `LeastUsed` silently exclude providers with `DailyRequestLimit: 0`; operators must be aware of this interaction
- The silent default on unknown strategy values trades strict validation for upgrade safety — a typo will not be caught at startup
- LlamaSharp's exclusion from strategy selection means operators cannot use it as a load-balanced peer provider without a sidecar workaround (see ADR-004)
