# C23 — Измерительный gate возможного C++

## 1. Назначение

На основании готового C# production candidate решить, существует ли компонент, для которого оправдан отдельный C++ prototype.

Это не задание на перенос и не разрешение добавлять C++ production code.

## 2. Архитектурный контекст

Прочитать:

- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, раздел 20;
- `docs/ADR-001_CSHARP_FIRST_RUNTIME.md`;
- measurements и acceptance report C22;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- `docs/operations/FINAL_ACCEPTANCE_REPORT.md`
- load/soak harness C22
- runtime/protocol metrics C21
- только исходные файлы конкретного измеренного hotspot, если он существует

Остальные части проекта не профилировать без связи с measured symptom.

## 4. Целевой результат

Один из двух выводов:

### A. Оставить C#

Нет доказанного узкого места либо ожидаемый выигрыш не оправдывает interop/deployment complexity. Это полноценный успешный результат задания.

### B. Рекомендовать ограниченный prototype

Указаны:

- точный компонент и stable boundary;
- воспроизводимый benchmark;
- baseline CPU/memory/allocations/latency/throughput;
- целевой измеримый критерий улучшения;
- interop contract и failure model;
- стоимость Windows deployment/diagnostics;
- план prototype, требующий отдельного разрешения пользователя.

## 5. Объём анализа

- Повторить representative measurement в Release без debugger.
- Использовать profiler/benchmark, соответствующий симптомам.
- Сначала проверить алгоритм, allocations, batching, PostgreSQL/network wait и configuration limits.
- Не считать I/O wait доказательством необходимости C++.
- При hotspot допускается добавить C# benchmark project; production behavior не менять.
- Создать `docs/operations/CPP_DECISION_REPORT.md`.

## 6. Архитектурные требования

- Не писать C++ code.
- Не менять public runtime contracts.
- Не выбирать большой модуль «на будущее».
- Не использовать один microbenchmark без end-to-end влияния.
- Учесть crash isolation, memory ownership, cancellation, telemetry и packaging.

## 7. Критерии приёмки

- Measurement воспроизводим и связан с пользовательским/эксплуатационным SLO.
- Вывод содержит численные данные.
- Рассмотрены C# optimizations и I/O bottlenecks.
- При варианте B boundary минимален и prototype имеет stop criteria.
- Без достаточных данных выбран вариант A.

## 8. Итоговый отчёт

Вернуть решение A/B, таблицу измерений и отдельный вопрос пользователю только если рекомендуется prototype.

