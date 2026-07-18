# ADR-003 — базовые semantic contracts

**Статус:** Accepted  
**Дата:** 19 июля 2026 года  
**Область:** `IG-02`, `S02`

## Контекст

До walking skeleton требуются единые semantics для identity, revision/version, result/error, observed values, времени и owner-specific progress. Они должны предотвращать неявное смешение значений без введения полной domain model или wire schema.

## Решение

1. Canonical identity — непустой GUID в `D`-формате, типизированный compile-time scope через `CanonicalId<TScope>`; неявные преобразования отсутствуют.
2. `RevisionNumber` и `StateVersion` являются разными монотонными типами и начинаются с единицы.
3. Ожидаемый outcome выражается `Result`/`Result<TValue>` и структурированной ошибкой со стабильным machine-readable code.
4. Наблюдаемое значение, unit, quality и freshness представлены отдельными типами без неявных преобразований и unit conversion.
5. Source, receive и processed timestamps являются разными UTC-only типами.
6. Wall и monotonic time имеют разные injectable interfaces; elapsed time вычисляется только по monotonic timestamps.
7. `OwnerPosition<TScope>` и `ConsumerCursor<TScope>` разделены как по назначению, так и по owner scope.

## Последствия

- Нельзя неявно смешивать ID, revision/version, timestamps, owner positions и cursors.
- Конкретные domain identities задают собственные scope marker types в соответствующих будущих модулях.
- Wire representation, domain entities, persistence mapping и unit conversion остаются вне `S02`.
- `IG-02` закрывается unit/property-style contract tests.
