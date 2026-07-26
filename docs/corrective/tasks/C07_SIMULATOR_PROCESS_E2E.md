# C07 — Межпроцессный Simulator E2E и recovery corpus

## 1. Назначение

Доказать, что результаты C01–C06 образуют работающую систему через реальные process boundaries, а не только in-process fixtures.

## 2. Архитектурный контекст

Прочитать:

- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 3, 6, 16, 17 и 19;
- `CORRECTIVE_ROADMAP.md`, Gate R2;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- executables и composition, созданные C01–C06
- `Dispatcher.slnx`
- `tests/Dispatcher.IntegrationTests/PostgreSqlClusterFixture.cs`
- `tests/Dispatcher.IntegrationTests/SimulatorActivationTests.cs`
- `tests/Dispatcher.IntegrationTests/CoreRuntimeRecoveryTests.cs`
- `tests/Dispatcher.IntegrationTests/ServerRealtimeTests.cs`
- `tests/Dispatcher.IntegrationTests/HistoryAcceptanceTests.cs`
- `tests/Dispatcher.IntegrationTests/AlarmEvaluationTests.cs`
- `tests/Dispatcher.IntegrationTests/EventDispatcherTests.cs`

Остальные файлы изучать по необходимости.

## 4. Целевой результат

Автоматический Windows E2E harness:

1. создаёт отдельную PostgreSQL 17 database;
2. запускает DatabaseMigrator как process;
3. готовит минимальные identity/configuration/simulator fixtures через production stores;
4. запускает RuntimeHost;
5. запускает Server на свободном loopback port;
6. выполняет production login;
7. получает Web index, HTTP snapshot и SignalR delta;
8. проверяет History sample, Alarm occurrence и Event;
9. проверяет restart RuntimeHost и Server;
10. проверяет fault/resync;
11. гарантированно останавливает процессы и очищает временные ресурсы.

## 5. Объём реализации

- Новый E2E test project либо чётко изолированный process test layer.
- Bounded startup/readiness waiting без произвольных sleeps.
- Captured stdout/stderr с redaction и выводом при failure.
- Уникальные ports, database и fixture identities.
- Process exit/crash detection.
- Тесты:
  - happy path;
  - RuntimeHost restart между delivery stages;
  - Server restart;
  - временная недоступность PostgreSQL либо контролируемый connection fault;
  - slow consumer до delta gap и последующий resnapshot;
  - graceful Ctrl+C/cancellation equivalent.

## 6. Архитектурные требования

- Не заменять processes общей DI container.
- Не использовать Docker.
- Не использовать реальную пользовательскую БД.
- Не сохранять fixture password/secret в repository.
- Не смягчать production auth ради теста.
- Fault injection должен быть детерминированным и иметь timeout.

## 7. Критерии приёмки

- E2E стабильно проходит повторно на Windows 11/PostgreSQL 17.
- Current становится видим только после History/Alarm/Event completion.
- Restart не создаёт duplicate history/event.
- Gap приводит к resnapshot.
- После теста не остаются processes и временная database.
- Общие unit/integration tests продолжают проходить.

## 8. За пределами задания

- Playwright и визуальная проверка.
- Modbus/SNMP.
- Windows service installation.
- Load/soak.

## 9. Итоговый отчёт

Привести сценарии, process topology, measured test duration и доказательство cleanup.

