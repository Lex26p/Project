# C02 — RuntimeHost и production polling Simulator

## 1. Назначение

Превратить `Dispatcher.RuntimeHost` из процесса, который только стартует и ожидает остановки, в реально работающий bounded runtime для Simulator.

## 2. Архитектурный контекст

Прочитать:

- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 3.2, 5.1, 7, 8 и 16;
- `docs/ADR-007_BOUNDED_RUNTIME_CURRENT.md`;
- `docs/ADR-011_SIMULATOR_COMMAND_SECURITY.md`;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- `src/Dispatcher.RuntimeHost/Program.cs`
- `src/Dispatcher.RuntimeHost/RuntimeProcess.cs`
- `src/Dispatcher.RuntimeHost/RuntimeHostOptions.cs`
- `src/Dispatcher.RuntimeHost/Dispatcher.RuntimeHost.csproj`
- `src/Dispatcher.Core/CoreRuntimeHost.cs`
- `src/Dispatcher.Core/CoreRuntimeStore.cs`
- `src/Dispatcher.Core/CoreRuntimeMigrations.cs`
- `src/Dispatcher.Core/PollScheduling.cs`
- `src/Dispatcher.Simulator/SimulatorRuntimeStore.cs`
- `src/Dispatcher.Simulator/SimulatorPollingSource.cs`
- `src/Dispatcher.Simulator/SimulatorRuntimeManifest.cs`
- `tests/Dispatcher.UnitTests/RuntimeSchedulingTests.cs`
- `tests/Dispatcher.UnitTests/SimulatorWalkingSkeletonTests.cs`
- `tests/Dispatcher.IntegrationTests/SimulatorActivationTests.cs`
- `tests/Dispatcher.IntegrationTests/CoreRuntimeRecoveryTests.cs`

Остальные файлы изучать по необходимости.

## 4. Целевой результат

При наличии active Simulator manifest RuntimeHost:

1. восстанавливает Core;
2. выделяет новое durable source session generation;
3. создаёт `SimulatorPollingSource`;
4. активирует binding в Core и scheduler;
5. периодически выполняет bounded poll;
6. нормализует результат через существующие runtime-контракты;
7. вызывает `EnqueueAsync` и последовательно `ProcessNextAsync`;
8. корректно останавливает polling, drain и PostgreSQL resources.

При отсутствии active manifest процесс остаётся живым, сообщает not-ready reason и выполняет bounded reconciliation retry без crash-loop.

## 5. Объём реализации

- Добавить RuntimeHost явную ссылку на Simulator.
- Сделать runtime loop отдельным testable компонентом, а не кодом внутри top-level `Program`.
- Дополнить options:
  - Simulator database role;
  - poll interval и timeout;
  - scheduler max bindings/max in-flight;
  - bounded reconciliation/backoff;
  - существующие ingress/current limits.
- Добавить durable allocator `SourceSessionGeneration` в новую Core migration version.
- Использовать `BoundedPollScheduler`; не создавать параллельный scheduler.
- Обеспечить fence stale completion после смены binding/session.
- Отделить ожидаемые source/reconciliation failures от fatal invariant failures.
- Сохранить graceful cancellation.

Допускается перевод RuntimeHost на .NET Generic Host, если это делает lifecycle тестируемым и готовым к C21. Не вводить HTTP API.

## 6. Архитектурные требования

- Один RuntimeHost обслуживает один configured scope.
- На этом этапе регистрируется только Simulator.
- Poll не начинается до Core recovery и active binding.
- Admission closed означает отсутствие новых polls.
- Session generation никогда не повторяется после restart.
- Отказ Simulator не помечается как отказ PostgreSQL.
- Никакая Simulator command не выполняется polling worker.

## 7. Критерии приёмки

- Integration test доказывает изменение Core current через production runtime worker.
- Restart выдаёт более новое session generation и не принимает stale result.
- No-manifest и temporary PostgreSQL failure не создают tight loop.
- Scheduler overlap/capacity/timeout остаются bounded и наблюдаемы.
- Cancellation завершает in-flight работу в ограниченное время.
- Existing Simulator/Core tests проходят.

## 8. За пределами задания

- Published current для Server.
- History/Alarm/Event wiring.
- Dynamic configuration distribution.
- Modbus и SNMP.
- Web и Windows service registration.

## 9. Итоговый отчёт

Указать фактический lifecycle worker, новые настройки и доказательства restart/session fencing.
