# C12 — Modbus TCP read-only production source: итоговый отчёт

## 1. Результат

Опубликованная whole-scope configuration с `modbus_tcp_read_only` теперь создаёт production Modbus TCP sources внутри RuntimeHost:

1. C11 reconciliation валидирует protocol plan и capacity до activation.
2. Для каждого Modbus source выделяется новая `SourceSessionGeneration`.
3. Production factory создаёт controller без secret и регистрирует точный generation set в `ProtocolRuntimeSupervisor`.
4. Simulator и Modbus используют общий `BoundedPollScheduler`.
5. Принятый poll создаёт `RuntimeCut` и проходит общий durable current → History → Alarm → Event pipeline.
6. При generation switch старые workers дренируются, controllers освобождаются, а late response fence-ится binding/session generation.

Server и Web не открывают Modbus TCP connections.

## 2. Поддерживаемый wire contract

- function codes: только FC03 `Read Holding Registers` и FC04 `Read Input Registers`;
- value types: signed/unsigned 16-bit и signed/unsigned 32-bit;
- явные unit ID, zero-based address, byte order и word order;
- decimal scale применяется после endian decoding; результат обязан точно представляться `Int64`, иначе point получает failure code `modbus.scale_result`;
- каждый configured point читается отдельным запросом, поэтому соседние адреса не объединяются за пределами явно заданного диапазона;
- production registration не содержит FC05/06/15/16 и других write operations.

## 3. Bounded execution и recovery

- attempts: `retry.maxAttempts`, диапазон 1–10;
- delay между attempts: `retry.delayMs`, с cancellation;
- общий I/O deadline: `DISPATCHER_RUNTIME_POLL_TIMEOUT_MS`;
- response bytes, observations и concurrent operations ограничены RuntimeHost settings;
- source/point/register capacity проверяется до activation;
- одна source failure не завершает polling остальных sources;
- disconnect, timeout или malformed response публикует последнее известное значение как `Bad/Stale`, но не как новое `Good/Fresh`;
- следующий успешный poll возвращает source в `Ready` и публикует `Good/Fresh`;
- worker snapshot содержит bounded counters, readiness и sanitised reason codes без host credentials.

Добавлены необязательные settings с безопасными bounded defaults:

- `DISPATCHER_RUNTIME_MODBUS_MAX_POINTS`;
- `DISPATCHER_RUNTIME_MODBUS_MAX_REGISTERS_PER_POLL`;
- `DISPATCHER_RUNTIME_PROTOCOL_MAX_RESPONSE_BYTES`;
- `DISPATCHER_RUNTIME_PROTOCOL_MAX_OBSERVATIONS`;
- `DISPATCHER_RUNTIME_PROTOCOL_MAX_CONCURRENT_OPERATIONS`.

## 4. Диагностика

Существующие connection test и sample poll сохранены на production source controller:

- connection test не меняет source position;
- sample poll декодирует значения, но не публикует их в runtime;
- наружу возвращаются только sanitised status/reason codes и samples;
- Modbus source не принимает secret reference.

Подключение durable diagnostic jobs и Engineering UI остаётся C14.

## 5. Изменённые области

### Production

- `src/Dispatcher.Modbus/ModbusTcpConfiguration.cs`
- `src/Dispatcher.Modbus/ModbusTcpCodec.cs`
- `src/Dispatcher.Modbus/ModbusTcpTransport.cs`
- `src/Dispatcher.ProtocolCommissioning/ProtocolCommissioningManifest.cs`
- `src/Dispatcher.Protocols/ProtocolRuntimeSupervisor.cs`
- `src/Dispatcher.Core/PollScheduling.cs`
- `src/Dispatcher.RuntimeHost/RuntimeHostOptions.cs`
- `src/Dispatcher.RuntimeHost/RuntimeConfigurationActivationPlan.cs`
- `src/Dispatcher.RuntimeHost/RuntimeConfigurationReconciler.cs`
- `src/Dispatcher.RuntimeHost/RuntimeProcess.cs`
- `src/Dispatcher.RuntimeHost/ProductionRuntimeHostSession.cs`
- `src/Dispatcher.RuntimeHost/ModbusRuntimeSourceFactory.cs`
- `src/Dispatcher.RuntimeHost/ProtocolPollingWorker.cs`

### Tests

- `tests/Dispatcher.UnitTests/ModbusTcpReadOnlyTests.cs`
- `tests/Dispatcher.IntegrationTests/FakeModbusTcpPeer.cs`
- `tests/Dispatcher.IntegrationTests/ModbusTcpRuntimeTests.cs`
- `tests/Dispatcher.IntegrationTests/RuntimeConfigurationReconciliationTests.cs`

## 6. Wire-level и pipeline evidence

Локальный Windows loopback fake Modbus TCP peer подтвердил:

- фактические запросы содержат только FC03/FC04;
- FC03/FC04 decoding, byte/word order и scale;
- short response отклоняется с `protocol.io_failed`;
- deadline возвращает `protocol.io_timeout`;
- disconnect восстанавливается внутри configured attempt limit;
- production disconnect переводит current в `Bad/Stale`, после reconnect значение снова становится `Good/Fresh`;
- Modbus observations записываются в published current, History, Alarm occurrence и Event journal.

Дополнительно проверены stale binding response и configured point/register capacity.

## 7. Проверки

- `dotnet restore Dispatcher.slnx --locked-mode` — успешно, все проекты актуальны;
- `dotnet build Dispatcher.slnx --configuration Release --no-restore` — успешно, 0 warnings, 0 errors;
- Unit — 143/143 успешно;
- целевые Modbus/protocol/scheduler Unit — 24/24 успешно;
- loopback Modbus TCP Integration — 3/3 успешно;
- production Modbus current/history/alarm/event acceptance — 1/1 успешно;
- C11/C12 protocol/runtime regression — 13/13 успешно;
- полный Integration исходного финального прогона — 130/133; три выявленных падения исправлены;
- повтор трёх упавших сценариев после исправления — 3/3 успешно, включая C07 process E2E.

## 8. Ограничения

- Physical device acceptance выполняется в C15.
- Modbus writes отсутствуют.
- SNMP production source относится к C13.
- Engineering diagnostics UI относится к C14.

## 9. Итог

Критерии C12 выполнены. Modbus TCP read-only подключён к production RuntimeHost configuration reconciliation, общему scheduler и durable pipeline; disconnect, timeout, malformed response, reconnect, capacity и generation fencing проверены без Docker, Linux и физической записи.
