# ADR-011 — Simulator command security baseline

**Status:** Accepted for `IG-07` / `S37–S38`  
**Date:** 22 July 2026

## Decision

`MOD-CMD` owns ControlLease, safety-block state and prepared command intent. A lease is short-lived, bound to one production session, subject and runtime scope, and can be revoked explicitly. It never replaces exact command and target permissions, which are checked again for every preparation request.

Configured step-up requires the Identity owner to revalidate the current local account password. The resulting in-memory attestation is bound to the same session/subject, has a short lifetime and is consumed once by the Command owner. Passwords and reusable step-up credentials are never stored in Command data.

Only a Simulator point can be prepared. The immutable prepared intent records the active configuration revision, revision number, manifest fingerprint and generation, the current position/value/quality/freshness, the command safety version, exact target and desired value. History-mode, stale configuration/current evidence, bad or stale quality, an active safety block, expired/revoked authority and missing target permission all fail closed.

S37 exposes no execute method or executor. S38 adds only a Simulator execution owner which repeats lease/session/permission/config/current/safety preflight, records accepted/progress/terminal transitions and creates an idempotent local Simulator receipt. The receipt is not protocol I/O and cannot affect a physical target. A timeout after receipt commit is persisted as `Unknown`; only reconciliation with the same execution identity and originating session may recover the prior durable result. A different execution identity cannot execute the same prepared intent and a new session cannot reuse an existing execution identity. Protocol assemblies remain read-only, physical qualification remains absent until explicit `IG-08` authorization, and production physical writes remain disabled through `S43` unless separately approved.

## Alternatives rejected

- A permission-only command path was rejected because it has no time-bounded holder authority or fencing.
- A client-generated step-up flag was rejected because it is not authentication evidence.
- A generic device/protocol command adapter was rejected because S37 is Simulator-only and physical writes are not authorized.

## Recovery and rollback

Leases, revocations, safety versions, prepared intents, execution transitions, Simulator receipts and audit are durable in the Command schema. Restart does not extend authority. Expired leases are closed before a new lease is admitted; prepared intents never execute by themselves. An accepted/in-progress execution without a receipt reconciles to `Unknown`, while an existing immutable receipt resolves the same identity to its previous success or rejection. Rollback is disabling the Command Server configuration and leaving all protocol write surfaces absent.
