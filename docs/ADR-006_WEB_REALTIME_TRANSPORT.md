# ADR-006 — Web/realtime transport

**Status:** Accepted  
**Date:** 2026-07-19  
**Gate:** `IG-05`

## Context

`S06` requires the first Server-to-Web current-value path. The transport must preserve the existing authority boundaries: Web never reads Core directly, every bootstrap and delta is authorized, realtime is not History, and production authentication remains outside this sprint.

## Decision

- Server request/response uses ASP.NET Core HTTP contracts; scoped realtime bootstrap and delta polling use SignalR.
- Server maps Core records to transport DTOs only after exact session and point permission checks.
- Each SignalR connection owns an opaque Web cursor and a private Core cursor. Hidden point changes advance only the private cursor and are not observable through Web positions or payloads.
- A cursor mismatch produces `Gap`; reconnect always requires a new bootstrap snapshot.
- Revocation, expiry, session replacement, or a changed visible point set invalidates the subscription and clears client state before a new authorization/bootstrap.
- Polling cadence and render cadence are separate: no-change polls do not render, and multiple changes in one catch-up delta are applied before one render request.
- Until production AuthN is implemented, test sessions are accepted only through an explicitly enabled bridge in `Development` or `Test`. The bridge is fail-closed in other environments.

## Consequences

- `IG-05` can close with authorized HTTP/SignalR smoke, gap/reconnect/permission fault, and slow-consumer catch-up evidence.
- The transport does not provide History, Dashboard editing, production IAM, protocol I/O, or a production retention/capacity limit.
- The initial Web client and Server may be deployed behind the same origin or an equivalent host configuration; production topology remains provisional.
