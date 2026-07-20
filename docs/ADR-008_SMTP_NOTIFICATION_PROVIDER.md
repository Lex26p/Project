# ADR-008 — Initial SMTP notification provider

**Status:** Accepted  
**Date:** 20 July 2026

## Decision

`MOD-NOT` uses SMTP as the initial production notification provider. The adapter uses the .NET SMTP client behind an owned interface, accepts only immutable delivery messages, and returns provider outcomes without mutating Event, Alarm or inbox read state.

Production configuration requires TLS, bounded timeout, a sender address and reference-only credentials. Raw credentials are resolved into a short-lived lease only for the provider call and are absent from configuration snapshots, obligations, attempts, receipts, errors and realtime payloads. A separate controlled-test profile may use loopback SMTP without TLS or credentials.

Delivery obligations and provider attempts are durable notification-owned records. Claiming is lease-based; recovery reuses the same attempt identity until an explicit provider outcome is accepted. Explicit transient failure schedules a bounded retry. Exhausted mandatory delivery becomes `EscalationRequired`; exhausted personal delivery becomes `TerminalFailure`. Provider timeout is not an Alarm/Event outcome.

## Consequences

- SMTP is the only provider in `S28`; additional providers are out of scope.
- Message identity is derived from the durable attempt identity and remains stable across crash recovery.
- Realtime exposes only authorized inbox counters, never provider credentials or hidden source metadata.
- Organization-specific SLO/escalation constants remain configuration inputs rather than product defaults.

## IG-13 evidence

`IG-13` may close only after controlled SMTP channel, outage/retry, duplicate acceptance, backlog/restart and secret-scrubbing tests pass.
