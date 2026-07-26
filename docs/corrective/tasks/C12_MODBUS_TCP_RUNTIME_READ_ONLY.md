# C12 — Modbus TCP read-only production source

## 1. Назначение

Подключить существующий Modbus TCP adapter к RuntimeHost configuration reconciliation и общей polling/pipeline композиции.

## 2. Архитектурный контекст

Прочитать:

- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 7, 8, 13, 14 и 16;
- `docs/ADR-003_SEMANTIC_CONTRACTS.md`;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- runtime source/reconciliation contracts C11
- `src/Dispatcher.Modbus/ModbusTcpConfiguration.cs`
- `src/Dispatcher.Modbus/ModbusTcpCodec.cs`
- `src/Dispatcher.Modbus/ModbusTcpTransport.cs`
- `src/Dispatcher.Protocols/ProtocolSourceContract.cs`
- `src/Dispatcher.Protocols/ProtocolRuntimeSupervisor.cs`
- `src/Dispatcher.ProtocolCommissioning/ProtocolCommissioningManifest.cs`
- `src/Dispatcher.RuntimeHost/RuntimeProcess.cs`
- `tests/Dispatcher.UnitTests/ModbusTcpReadOnlyTests.cs`
- `tests/Dispatcher.UnitTests/ProtocolRuntimeContractTests.cs`
- `tests/Dispatcher.IntegrationTests/ProtocolCommissioningAcceptanceTests.cs`

Остальные файлы изучать по необходимости.

## 4. Целевой результат

Опубликованная valid Modbus configuration создаёт в RuntimeHost read-only source controller, который:

- подключается по TCP;
- читает только FC03/FC04;
- декодирует configured points;
- проходит общий scheduler/session fencing;
- создаёт `RuntimeCut`;
- попадает в current/history/alarm/event pipeline;
- восстанавливается после disconnect в заданных bounded limits.

## 5. Объём реализации

- Production source factory/registration на основе `ProtocolActivationPlan.ModbusSources`.
- Связь с `ProtocolRuntimeSupervisor`, scheduler и secret-free workload configuration.
- Lifecycle disposal/reconnect при generation switch.
- Безопасная диагностика connection/sample для последующего C14.
- Structured metrics/reason codes без host credentials.
- Deterministic tests с локальным fake Modbus TCP peer:
  - FC03;
  - FC04;
  - byte/word order и scale;
  - malformed/short response;
  - timeout;
  - reconnect;
  - stale generation;
  - configured capacity.

## 6. Архитектурные требования

- Production code не содержит FC05/06/15/16 execution.
- Не объединять соседние адреса за пределами configured safe range.
- Не выполнять retry без limits/cancellation.
- Ошибка одного source не останавливает другие.
- Последнее значение после disconnect не выдаётся как fresh/good.
- Server/Web не открывают TCP connection.

## 7. Критерии приёмки

- Fake peer видит только function code 3 или 4.
- Значения проходят до published current и History.
- Disconnect отражается quality/readiness и восстанавливается.
- Stale response после reconfiguration отбрасывается.
- All Modbus/protocol/runtime tests проходят.

## 8. За пределами задания

- Реальное устройство C15.
- Modbus writes.
- Engineering UI C14.

## 9. Итоговый отчёт

Указать supported function codes/types, retry limits и wire-level test evidence.

