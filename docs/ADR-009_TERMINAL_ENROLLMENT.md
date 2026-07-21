# ADR-009 — Terminal enrollment and credential baseline

**Status:** Accepted for `S33` / `IG-12`  
**Date:** 22 July 2026

## Decision

`MOD-TRM` owns terminal fleet records, enrollment challenges, device identities, credential hashes, profiles, content assignments and presence. A `TerminalId`, a `TerminalDeviceIdentityId` and a shared `TerminalProfileId` are distinct identities.

Enrollment uses a cryptographically random one-time challenge with configured lifetime, explicit operator approval under `terminals.enrollment.approve`, and atomic one-time exchange. Successful exchange issues an opaque 256-bit credential for a separately configured lifetime. The raw challenge and credential are returned only once; PostgreSQL stores only their SHA-256 hashes. Terminal authentication accepts only the `Dispatcher-Terminal` authorization scheme. Query parameters are never an identity source.

Blocking or revoking a terminal immediately denies authentication, assigned content and presence updates. Credential expiry and revocation are checked against durable owner state on every request. Recovery reads the same durable identity and hash state; no raw credential is reconstructed.

## Rejected alternatives

- Query-string tokens: leak through URLs/logs and are not accepted as identity.
- Shared profile credential: merges device identities and prevents per-terminal revoke/audit.
- Password-like pairing code stored reversibly: weaker storage and replay properties.
- Vendor hardware attestation or automated certificate fleet: outside the accepted sprint scope.

## Evidence required to close IG-12

Expiry, concurrent exchange/replay, block/revoke, restart recovery, separate identities under a shared profile, header-only identity resolution and hash-only credential storage are integration-tested on Windows x64.
