# ADR-005 — session/security и platform nucleus

**Статус:** Accepted  
**Дата:** 19 июля 2026 года  
**Область:** `IG-04`, `S04`

## Контекст

До первого ingress требуются fail-closed session semantics, exact effective permissions, durable idempotency/audit admission и минимальный operational substrate. Production authentication и IAM administration относятся к `IG-04P`/`S35` и не должны имитироваться в foundation.

## Решение

1. Session всегда имеет typed session/subject identity, principal kind (`User`, `Device`, `Workload`), UTC issuance/expiry, optional revocation и explicit effective permissions.
2. Mutation требует непустую активную session и точный permission grant. Denial имеет приоритет; wildcard и имя `admin` не дают bypass.
3. Test identity выдаётся только при одновременных explicit enablement и environment `Development`/`Test`, с явно переданными permissions и lifetime не более восьми часов. В production profile выдача fail closed.
4. Operation admission хранит typed operation identity, subject-scoped idempotency key и request fingerprint. Первичная admission и audit record коммитятся в одной PostgreSQL transaction; повтор с тем же fingerprint возвращает прежний operation ID, несовместимый повтор — conflict.
5. Platform schema принадлежит отдельной database role и содержит только admission/audit nucleus и минимальную durable job queue.
6. Durable job использует `available_at`, bounded lease, worker identity и attempts. Истёкшая lease допускает безопасный reclaim; workflow/routing semantics отсутствуют.
7. Structured observability использует `ActivitySource` и `Meter`; liveness отражает живой process, readiness требует доступной migrated platform schema.

## Последствия

- Anonymous, revoked, expired и insufficient-permission mutations блокируются до persistence.
- Production credential validation, refresh/recovery, account/role administration и external IdP остаются в `S35`.
- Audit delivery после admission использует durable obligation/job в последующих consumers; текущий audit admission не является полным Audit Journal.
- Generic workflow engine, retries policy и external ingress отсутствуют.
