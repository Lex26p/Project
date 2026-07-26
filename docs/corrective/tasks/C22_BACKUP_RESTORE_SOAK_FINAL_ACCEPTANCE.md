# C22 — Backup/restore, load/soak и финальная Windows-приёмка

## 1. Назначение

Проверить восстановимость, bounded поведение и эксплуатационную устойчивость всего production candidate.

## 2. Архитектурный контекст

Прочитать:

- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 16–19;
- `CORRECTIVE_ROADMAP.md`, Gate R5;
- `docs/ACCEPTANCE_AND_TEST_STRATEGY.md`;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- Windows deployment/runbook C21
- process E2E C07
- protocol lab report C15
- все test projects
- все module migrations
- runtime published/delivery metrics C03/C04
- Server/Web browser tests C08–C20

Остальные файлы изучать по необходимости.

## 4. Целевой результат

Созданы и выполнены:

- PostgreSQL backup procedure;
- restore в отдельную clean database;
- post-restore integrity/readiness verification;
- representative load corpus;
- длительный soak;
- restart/fault matrix;
- итоговый acceptance report с доказательствами и ограничениями.

## 5. Объём реализации

- PowerShell wrappers/runbook для `pg_dump`/`pg_restore` или принятого PostgreSQL 17 механизма без credentials.
- Проверка всех authoritative schemas, migration histories и необходимых protected data.
- Restore не выполняется поверх рабочей БД.
- Load profiles:
  - configured maximum Simulator points/sources в обоснованной тестовой границе;
  - slow/no Web consumers;
  - repeated reconnect;
  - History/Alarm/Event activity;
  - concurrent authorized Web sessions.
- Fault matrix:
  - RuntimeHost/Server kill/restart;
  - temporary PostgreSQL outage;
  - downstream failure;
  - source timeout;
  - session revoke;
  - delta gap;
  - migration mismatch rejection.
- Контроль memory, CPU, queue depth, delivery lag, database growth и latency percentiles.
- `docs/operations/FINAL_ACCEPTANCE_REPORT.md`.

## 6. Архитектурные требования

- Workload не превращается в произвольный production limit без записи measured context.
- Soak не использует реальные устройства выше согласованной частоты; основной load создаёт Simulator.
- Нет unbounded memory/delta/job/log growth.
- Restore содержит проверку, а не только успешный exit utility.
- Failed criterion фиксируется честно, не исключается из отчёта.

## 7. Критерии приёмки

- Backup восстанавливается в отдельную PostgreSQL 17 database.
- После restore login, Simulator current, History, Alarm/Event и Web работают.
- Fault matrix не теряет protected pending delivery и не создаёт duplicates.
- Ресурсы стабилизируются либо рост объяснён bounded retention.
- Все automated suites проходят.
- Report содержит версии среды, duration, workload, результаты и открытые риски без сведений о системе контроля версий.

## 8. За пределами задания

- Disaster recovery второго site.
- Linux/Docker.
- Physical command tests.
- C++ implementation.

## 9. Итоговый отчёт

Привести ссылки на backup/restore evidence, workload measurements, fault results и итоговую рекомендацию о production readiness.

