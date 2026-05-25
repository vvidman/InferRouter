# ADR-005 — Docker Secrets for API Keys

**Status:** Accepted  
**Date:** 2026-05-25

---

## Context

InferRouter holds API keys for every cloud provider in its chain. These keys must be available to the running process but must not appear in:

- Source control (Git)
- Docker images
- Container environment variables (visible to all processes in the container and in `docker inspect` output)
- `appsettings.json` (committed to the repo)

Common approaches in .NET projects include `.env` files, environment variables in `docker-compose.yml`, and user secrets. Each has drawbacks in a containerized deployment context.

---

## Decision

API keys are provided exclusively via **Docker Secrets**, mounted as files at `/run/secrets/<secret_name>` inside the container.

At startup, InferRouter reads each provider's key from the corresponding file path derived from the provider's `Name` in config:

```
/run/secrets/{provider_name}_api_key
```

For example, a provider named `groq` expects its key at `/run/secrets/groq_api_key`.

**Repository layout:**

```
secrets/                        ← git-ignored
    groq_api_key.txt
    gemini_api_key.txt

secrets.example/                ← committed
    groq_api_key.txt            ← contains literal text: "your-groq-api-key-here"
    gemini_api_key.txt
```

`secrets.example/` serves as setup documentation. A first-time user copies it to `secrets/` and fills in real values.

---

## Why Not Environment Variables?

Environment variables are visible to all processes in the container and appear in plain text in `docker inspect` output. They are also easy to accidentally commit via `.env` files. For a project whose primary concern is holding third-party API keys, this risk is unnecessary.

## Why Not .NET User Secrets?

User secrets are a development-time tool and have no Docker integration. They do not solve the production secret management problem.

## Why Not a Secret Manager (Vault, AWS Secrets Manager)?

InferRouter is designed for self-hosted, single-host, personal use. The operational overhead of running or subscribing to a secret manager is disproportionate. Docker Secrets provide file-based isolation without any additional infrastructure.

---

## Missing Key Behaviour

If a provider's secret file does not exist or is empty at startup, the provider **remains in the chain** but is treated as permanently in an `auth_error` state. This is consistent with ADR-006: a missing key is semantically identical to an invalid key — the provider cannot authenticate, so the `FallbackChainExecutor` skips it and moves to the next provider in the chain, exactly as it would after receiving a live `401` response.

This means:
- No special startup-time removal logic is needed
- The fallback chain handles the missing key transparently at request time
- The `rate_limit_hit` / `infer_fallback` log entries provide a clear audit trail of why the provider was skipped
- If the key file appears later (e.g. the user mounts it after startup), a container restart picks it up — no hot-reload is needed

InferRouter does log a warning at startup for each provider whose secret file is not found, so the misconfiguration is visible without needing to inspect the operation log.

---

## Consequences

**Positive:**
- Keys never appear in environment variables, images, or config files
- Standard Docker Compose feature — no additional tooling required
- Clear separation: `appsettings.json` holds structure, `secrets/` holds credentials

**Negative:**
- Slightly more setup friction for first-time users compared to a plain `.env` file
- `secrets.example/` must be kept in sync with the actual expected secret names as providers are added
- A missing key causes silent fallback at request time if the startup warning is not noticed. This is by design — the chain degrades gracefully — but could be surprising if the user expects a hard failure on misconfiguration
