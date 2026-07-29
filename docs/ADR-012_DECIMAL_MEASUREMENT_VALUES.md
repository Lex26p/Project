# ADR-012 — decimal-контракт измеряемых значений

**Статус:** Accepted  
**Дата:** 29 июля 2026 года  
**Область:** telemetry semantics, protocol normalization, persistence

## Контекст

Лабораторная приёмка C15 подтвердила реальные Modbus/SNMP значения с дробной инженерной размерностью: например, raw `2313` при scale `0.1` означает `231.3 V`. Существующий walking-skeleton контракт `TypedValue<long>` отклоняет такой результат либо требует потери точности.

`ADR-003` фиксирует разделение value, unit, quality и freshness, но не закрепляет конкретное числовое представление измерений.

## Решение

1. Измеряемые инженерные значения представлены `decimal` end-to-end.
2. `TypedValue<TValue>` сохраняется; telemetry использует `TypedValue<decimal>`.
3. Positions, cursors, versions, generations, IDs, seed/PRNG state, progress и operational counters остаются целыми.
4. Protocol boundary вычисляет `engineeringValue = rawValue × Scale` без преобразования к `long`.
5. Scale non-zero; результат обязан иметь не более девяти дробных знаков и абсолютное значение меньше `10^19`.
6. Samples, current, History minimum/maximum и Alarm comparisons не округляются.
7. History average округляется по явной versioned policy до девяти дробных знаков.
8. Canonical unit берётся из versioned point/configuration contract и сохраняется вместе со значением.
9. PostgreSQL хранит измерения как unconstrained `numeric` с fail-closed `CHECK`, эквивалентным envelope `numeric(28,9)`.
10. Старые и новые процессы не работают с одной БД одновременно; переход выполняется offline одним выпуском.

## Совместимость

- Новые migrations преобразуют только measurement-колонки из `bigint` в `numeric`.
- JSON obligations/checkpoints получают `MeasurementSemanticVersion = 2`.
- Отсутствующая версия означает legacy v1; compatibility reader без потери преобразует только целые v1 значения в decimal.
- Новые записи создаются только в v2.
- Physical protocol boundary остаётся read-only: Modbus только FC03/FC04, SNMP только GET.

## Последствия

- C15 остаётся заблокированным до завершения C14A и повторной проверки полного physical pipeline.
- Fixed-point storage units и скрытое округление не применяются.
- Simulator command engineering values переходят на decimal, но physical hard deny не меняется.

