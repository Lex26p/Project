# ADR-004 — PostgreSQL persistence и owner boundaries

**Статус:** Accepted  
**Дата:** 19 июля 2026 года  
**Область:** `IG-03`, `S03`

## Контекст

Модулям требуется один initial database baseline без общей модели владения. Persistence foundation должен обеспечивать последовательные migrations, fail-closed transaction semantics, воспроизводимый lifecycle тестовой БД и запрет direct foreign writes.

## Решение

1. Initial database technology — PostgreSQL 17+ через Npgsql.
2. Каждый authoritative owner получает отдельные PostgreSQL schema, database role, migration plan и migration history. Migration account может `SET ROLE` только для применения owner plan; runtime connection использует роль своего owner.
3. Миграции owner выполняются последовательно в одной transaction под advisory lock. Изменение SQL уже применённой версии определяется checksum mismatch и блокируется.
4. Cross-owner чтение в будущем выполняется через явно принятый contract/projection. Runtime roles не получают write privileges на чужие schemas.
5. Переход authoritative state и обязательная внешняя obligation принимаются в одной owner transaction. Obligation хранится owner-owned до подтверждённой передачи; прямой write в schema другого owner запрещён.
6. Integration tests используют отдельный временный PostgreSQL cluster и новую database для каждого сценария; существующий пользовательский instance не изменяется.

## Data classification и recovery

| Класс | Owner/transaction boundary | Recovery |
|---|---|---|
| Authoritative state | Один module owner и его schema | Backup/restore обязательны; migration rollback — restore либо forward fix |
| Mandatory obligation | Создаётся атомарно с owner transition | Durable retry/reconciliation до acceptance потребителем |
| Protected journal/audit | Отдельный authoritative owner, add-only semantics | Backup/retention обязательны; не восстанавливается из realtime |
| Rebuildable projection | Projection owner, только contract input | Перестраивается из authoritative source; не является source of truth |
| Secret material | Выделенное protected storage/access boundary | Только защищённый backup; plaintext не попадает в logs/audit |
| Ephemeral/cache | Не authoritative | Не восстанавливается; безопасно пересоздаётся |

History capacity, retention и отдельная topology квалифицируются позже и не выводятся из этого baseline.

## Рассмотренные альтернативы

- SQLite отклонён как общий baseline: не даёт требуемой parity для PostgreSQL roles/schemas/concurrency.
- SQL Server отклонён: добавляет второй platform baseline без требования.
- Отдельная database/service на каждый модуль отклонена до extraction evidence; logical ownership достаточно обеспечить schemas/roles.
- Generic repository и общая schema отклонены как размывающие owner semantics.

## Последствия

- `IG-03` закрывается fresh/repeat migration, checksum, rollback, atomic obligation и role-isolation integration tests.
- Database roles и secrets provisioned deployment-механизмом; они не хранятся в repository.
- Полные schemas будущих модулей, backup automation, historian scale и broker остаются вне `S03`.
