# C21 — Windows services, health, observability и runbook

## 1. Назначение

Сделать production candidate воспроизводимо устанавливаемым, запускаемым и диагностируемым на Windows 11.

## 2. Архитектурный контекст

Прочитать:

- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 16–18;
- `CORRECTIVE_ROADMAP.md`, Gate R5;
- `docs/ADR-004_POSTGRESQL_PERSISTENCE.md`;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- `src/Dispatcher.DatabaseMigrator/*`
- `src/Dispatcher.RuntimeHost/*`
- `src/Dispatcher.Server/*`
- `src/Dispatcher.Platform/PlatformDiagnostics.cs`
- `src/Dispatcher.Platform/PlatformHealth.cs`
- `src/Dispatcher.Administration/*`
- process E2E C07
- protocol acceptance C15
- existing project configuration files

Остальные файлы изучать по необходимости.

## 4. Целевой результат

Подготовлен Windows deployment:

- DatabaseMigrator запускается one-shot до services;
- RuntimeHost и Server устанавливаются/работают как Windows services;
- Server публикует Web;
- start/stop/restart и machine reboot имеют определённый порядок;
- минимальные liveness/readiness доступны локально;
- authorized operations view показывает подробное здоровье;
- structured logs, metrics и traces позволяют диагностировать source/pipeline/session;
- все настройки имеют проверяемые templates без secrets;
- создан эксплуатационный runbook.

## 5. Объём реализации

- Generic Host/Windows Service integration для RuntimeHost и Server.
- Graceful shutdown timeout и non-zero fatal exit behavior.
- PowerShell scripts для validate/install/start/stop/status/uninstall либо эквивалентный воспроизводимый механизм.
- Конфигурационные templates для Windows/PostgreSQL roles без значений secrets.
- Liveness/readiness endpoints и RuntimeHost heartbeat.
- Correlation IDs, structured safe logs, key counters/histograms:
  - poll attempts/outcomes/timeouts;
  - ingress/delivery lag;
  - published cursor/delta pruning;
  - source reconnect;
  - HTTP/SignalR sessions/gaps;
  - durable job outcomes.
- `docs/operations/WINDOWS_DEPLOYMENT_RUNBOOK.md`.
- Automated smoke установки там, где не требуются administrative actions; manual privileged steps описать.

## 6. Архитектурные требования

- Scripts не содержат credentials.
- Environment/appsettings precedence описан.
- Process liveness не выдаётся за operational readiness.
- Server может быть live при degraded runtime, но overall health это показывает.
- Logs не содержат token/community/connection string.
- Не добавлять Docker/Linux instructions.

## 7. Критерии приёмки

- Fresh Windows sequence воспроизводим по runbook.
- Services корректно переживают stop/start и reboot.
- Missing migration/role/secret даёт безопасный not-ready/failure.
- Operations UI различает DB/runtime/source/downstream faults.
- Log redaction tests проходят.
- E2E C07 и protocol tests проходят в service-like composition.

## 8. За пределами задания

- Backup/restore и soak C22.
- Installer GUI/MSI, если он не нужен для воспроизводимой установки.
- Enterprise monitoring backend.

## 9. Итоговый отчёт

Указать service topology, настройки, health endpoints, telemetry и privileged manual steps.

