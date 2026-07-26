# C13 — SNMP v2c read-only production source

## 1. Назначение

Подключить существующий SNMP v2c adapter к RuntimeHost configuration reconciliation и общей polling/pipeline композиции.

## 2. Архитектурный контекст

Прочитать:

- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 7, 8, 13, 14 и 16;
- `docs/ADR-003_SEMANTIC_CONTRACTS.md`;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- runtime source/reconciliation contracts C11
- `src/Dispatcher.Snmp/SnmpV2cConfiguration.cs`
- `src/Dispatcher.Snmp/SnmpV2cCodec.cs`
- `src/Dispatcher.Snmp/SnmpV2cTransport.cs`
- `src/Dispatcher.Protocols/ProtocolSecurity.cs`
- `src/Dispatcher.Protocols/ProtocolSourceContract.cs`
- `src/Dispatcher.Protocols/ProtocolRuntimeSupervisor.cs`
- `src/Dispatcher.ProtocolCommissioning/ProtocolCommissioningManifest.cs`
- `src/Dispatcher.RuntimeHost/RuntimeHostOptions.cs`
- `tests/Dispatcher.UnitTests/SnmpV2cReadOnlyTests.cs`
- `tests/Dispatcher.UnitTests/ProtocolRuntimeContractTests.cs`
- `tests/Dispatcher.IntegrationTests/ProtocolCommissioningAcceptanceTests.cs`

Остальные файлы изучать по необходимости.

## 4. Целевой результат

Опубликованная valid SNMP configuration создаёт в RuntimeHost source controller, который:

- получает community только через `ProtocolSecretReference`;
- выполняет только SNMP GET;
- проверяет request ID, response shape, OID и wire limits;
- проходит scheduler/session fencing;
- создаёт `RuntimeCut`;
- попадает в current/history/alarm/event pipeline;
- bounded восстанавливается после timeout/disconnect.

## 5. Объём реализации

- Production source factory/registration на основе `ProtocolActivationPlan.SnmpSources`.
- Подключение `EnvironmentProtocolSecretResolver` или эквивалентного workload resolver.
- Отсутствие plaintext secret в DTO, logs, exception и persisted diagnostic result.
- Lifecycle disposal/reconfiguration.
- Безопасная connection/sample diagnostics для C14.
- Deterministic tests с локальным fake SNMP peer:
  - GET response;
  - multiple configured OIDs в limits;
  - wrong request ID/community-safe rejection;
  - malformed/oversized packet;
  - timeout/retry;
  - stale generation;
  - missing secret.

## 6. Архитектурные требования

- SNMP SET не реализуется и не регистрируется.
- Не выполнять неограниченный walk.
- Community не сохраняется в configuration response и audit.
- Missing secret делает source not ready, но не раскрывает reference value сверх безопасной identity.
- Server/Web не открывают UDP connection.

## 7. Критерии приёмки

- Fake peer получает только GET PDU.
- Значения доходят до published current и History.
- Timeout/retry остаются в configured limits.
- Secret отсутствует в captured application logs.
- Stale response отбрасывается.
- All SNMP/protocol/runtime tests проходят.

## 8. За пределами задания

- SNMP v3.
- Traps/informs.
- GETBULK/walk.
- Реальное устройство C15.
- Engineering UI C14.

## 9. Итоговый отчёт

Указать supported PDU/value types, wire limits, secret boundary и retry evidence.

