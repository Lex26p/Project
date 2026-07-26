# C20 — Simulator-only command UX и physical hard deny

## 1. Назначение

Завершить безопасный command lifecycle для Simulator и доказать отсутствие пути физических команд Modbus/SNMP.

## 2. Архитектурный контекст

Прочитать:

- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, раздел 15;
- `WEB_INTERFACE_SPECIFICATION.md`, раздел 11;
- `WEB_BACKEND_API_REQUIREMENTS.md`, раздел 5.6;
- `docs/ADR-011_SIMULATOR_COMMAND_SECURITY.md`;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- `src/Dispatcher.Command/*`
- `src/Dispatcher.Server/CommandEndpoints.cs`
- `src/Dispatcher.Server/Program.cs`
- `src/Dispatcher.Simulator/SimulatorRuntimeStore.cs`
- `src/Dispatcher.Web/ControlApiClient.cs`
- `src/Dispatcher.Web/CommandRealtimeClient.cs`
- `src/Dispatcher.Web/Pages/Control.razor`
- Dashboard/kiosk C10
- Modbus/SNMP registration C12/C13
- `tests/Dispatcher.IntegrationTests/CommandExecutionTests.cs`
- `tests/Dispatcher.IntegrationTests/CommandSecurityTests.cs`
- `tests/Dispatcher.UnitTests/AlarmActionContractTests.cs`

Остальные файлы изучать по необходимости.

## 4. Целевой результат

Для Simulator Web реализует:

`Acquire lease → Prepare → Confirm → Execute → Observe terminal result → Release/expiry`.

UI показывает target, effect, policy, blocking reasons, expiry и uncertainty. Offline/stale/revoked conditions запрещают execution.

Для Modbus/SNMP:

- Server не создаёт executable intent;
- RuntimeHost не регистрирует physical command adapter;
- kiosk wallboard не получает lease;
- настройка приложения не может включить write path.

## 5. Объём реализации

- Завершить Web command workflow и session refresh/revoke behavior.
- Подключить command realtime с актуальным session header.
- Сохранить idempotency key, expected version и stable target.
- Отобразить accepted/dispatched/confirmed/failed/uncertain как разные состояния.
- Добавить architecture/security tests, сканирующие production registrations и protocol code paths на отсутствие write executor.
- Browser E2E happy/failure/expiry/reconnect и kiosk deny.

## 6. Архитектурные требования

- Открытый preflight не переназначается при смене selection.
- Offline command не ставится в очередь.
- Lease не заменяет object-level permission.
- Configuration change инвалидирует intent.
- Physical hard deny не является UI-only флагом.
- Не добавлять Modbus write codec или SNMP SET «для будущего».

## 7. Критерии приёмки

- Simulator command проходит полный lifecycle и audit.
- Duplicate execute не выполняется второй раз.
- Expired/revoked/stale intent отклоняется.
- Modbus/SNMP target не может пройти Prepare/Execute.
- Kiosk wallboard deny подтверждён Server и browser tests.
- Existing command/security tests проходят.

## 8. За пределами задания

- Любая физическая команда.
- Сценарии/расписания управления.
- C++.

## 9. Итоговый отчёт

Указать command states, security tests и все уровни physical deny.

