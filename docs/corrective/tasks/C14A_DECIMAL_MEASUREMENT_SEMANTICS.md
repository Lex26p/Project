# C14A — Decimal semantics измеряемых значений

## 1. Назначение

Атомарно заменить walking-skeleton `long` для инженерных telemetry-значений на точный `decimal` end-to-end до завершения C15.

## 2. Архитектурный контекст

Прочитать:

- `ADR-003_SEMANTIC_CONTRACTS.md`;
- `ADR-012_DECIMAL_MEASUREMENT_VALUES.md`;
- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 5, 8, 14 и 19;
- `TASK_EXECUTION_RULES.md`.

## 3. Объём реализации

- `SourceObservation`, current/published current и recovery contracts.
- Modbus/SNMP normalized values, diagnostics и last-known caches.
- Equipment diagnostic persistence/API/Web.
- History samples и aggregates.
- Alarm threshold, hysteresis, evaluator и persisted last value.
- Server/Web current, history, dashboard/mimic и SignalR DTO.
- Simulator baseline/amplitude/output и simulator command engineering values.
- PostgreSQL migrations и legacy JSON compatibility.

Positions, versions, generations, IDs, seed/PRNG state, progress и operational counters не изменяются.

## 4. Числовой контракт

- CLR: `decimal`.
- PostgreSQL: `numeric` плюс owner-local fail-closed checks.
- Envelope: до 28 значащих цифр, до 9 дробных знаков, `abs(value) < 10^19`.
- Protocol formula: `raw × Scale`.
- Неявное округление samples/current/alarms запрещено.
- History average использует явно зафиксированное округление до 9 дробных знаков.

## 5. Миграция

- Только новые migration versions.
- Existing integer values преобразуются без изменения математического значения.
- Переход offline; rolling deployment разных semantic versions запрещён.
- JSON obligations/checkpoints: legacy v1 integer reader, только v2 decimal writer.
- Server/Web fail closed при несовпадении measurement semantic version.

## 6. Критерии приёмки

- `2313 × 0.1 = 231.3m V` точно проходит diagnostic → observation → obligation/checkpoint → current/delta → History → API/SignalR → Web.
- Simulator публикует `21.5m °C` без изменения seed/position semantics.
- Alarm с дробными threshold/hysteresis поднимается и очищается, включая restart/replay.
- History sample/min/max точны; average следует decimal policy.
- Existing integer PostgreSQL data мигрирует без изменения значения.
- Legacy JSON восстанавливается в decimal и сохраняет idempotency.
- Непредставимые scale results и overflow отклоняются без округления.
- Physical read-only boundary и bounded processing не изменены.
- Проверки выполняются на Windows 11/PostgreSQL 17 без Docker/Linux.

## 7. Итоговый отчёт

Указать изменённые semantic/persistence boundaries, migration compatibility, decimal policy, regression results и готовность возобновить C15.

