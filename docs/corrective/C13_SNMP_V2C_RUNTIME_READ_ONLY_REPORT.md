# C13 — SNMP v2c read-only production source: итоговый отчёт

## 1. Результат

Опубликованная whole-scope configuration с `snmp_v2c_read_only` теперь создаёт production SNMP sources внутри RuntimeHost:

1. C11 reconciliation валидирует SNMP plan и capacity до activation.
2. Для каждого SNMP source выделяется новая `SourceSessionGeneration`.
3. Production factory создаёт controller с `ProtocolSecretReference`, а community разрешается только на время запроса.
4. Simulator, Modbus и SNMP используют общий `BoundedPollScheduler`.
5. Принятый GET response создаёт `RuntimeCut` и проходит общий current → History → Alarm → Event pipeline.
6. При generation switch старый controller освобождается, а late response отбрасывается fencing-проверкой.

Server и Web не открывают SNMP UDP connections.

## 2. Поддерживаемый wire contract

- request PDU: только SNMP v2c GET (`0xA0`);
- response PDU: SNMP response (`0xA2`);
- value types: `Signed32`, `Counter32`, `Gauge32`, `TimeTicks`, `Counter64`;
- каждый response проверяется по version, community, request ID, error status, числу varbind, OID и value type;
- multiple configured OIDs отправляются одним bounded GET request;
- SET, GETBULK, walk, traps и informs не реализованы и не регистрируются.

## 3. Wire limits и bounded recovery

Production settings ограничивают:

- количество points на source;
- число arcs и encoded bytes в OID;
- длину community;
- размер GET request и response;
- число observations и concurrent operations.

Retry выполняется в пределах `retry.maxAttempts` (1–10), с configured delay и response timeout на каждую попытку. Общий poll также ограничен RuntimeHost deadline. Timeout, malformed или oversized response публикует последнее известное значение как `Bad/Stale`; следующий корректный poll возвращает `Good/Fresh`.

## 4. Secret boundary

- configuration и activation plan содержат только `ProtocolSecretReference`;
- production RuntimeHost использует `EnvironmentProtocolSecretResolver`;
- community существует только внутри disposable secret lease и очищаемого request buffer;
- plaintext community не попадает в DTO, persistence, diagnostic result и тексты ошибок;
- missing secret переводит source в not-ready и создаёт безопасный `Bad/Stale` cut.

Connection test и sample poll сохранены на source controller для C14: они возвращают только sanitised status/reason codes, не публикуют sample в runtime и не раскрывают community.

## 5. Изменённые области

### Production

- `src/Dispatcher.Snmp/SnmpV2cConfiguration.cs`
- `src/Dispatcher.Snmp/SnmpV2cCodec.cs`
- `src/Dispatcher.Snmp/SnmpV2cTransport.cs`
- `src/Dispatcher.ProtocolCommissioning/ProtocolCommissioningManifest.cs`
- `src/Dispatcher.RuntimeHost/RuntimeHostOptions.cs`
- `src/Dispatcher.RuntimeHost/RuntimeConfigurationReconciler.cs`
- `src/Dispatcher.RuntimeHost/ProductionRuntimeHostSession.cs`
- `src/Dispatcher.RuntimeHost/ModbusRuntimeSourceFactory.cs`
- `src/Dispatcher.RuntimeHost/SnmpRuntimeSourceFactory.cs`
- `src/Dispatcher.RuntimeHost/ProtocolPollingWorker.cs`

### Tests

- `tests/Dispatcher.UnitTests/SnmpV2cReadOnlyTests.cs`
- `tests/Dispatcher.UnitTests/RuntimeHostOptionsTests.cs`
- `tests/Dispatcher.IntegrationTests/FakeSnmpUdpPeer.cs`
- `tests/Dispatcher.IntegrationTests/SnmpV2cRuntimeTests.cs`
- `tests/Dispatcher.IntegrationTests/RuntimeConfigurationReconciliationTests.cs`

## 6. Acceptance evidence

Локальный Windows loopback fake SNMP peer подтвердил:

- peer получает только GET PDU;
- один request содержит несколько configured OIDs;
- wrong request ID и wrong community безопасно отклоняются;
- malformed и oversized datagrams отклоняются;
- timeout/retry/recovery укладываются в configured attempt limit;
- missing secret не отправляет UDP request и не раскрывает community;
- stale response после generation switch отбрасывается;
- production SNMP observations доходят до published current, History, Alarm occurrence и Event journal;
- удаление secret переводит current в `Bad/Stale`, восстановление secret возвращает `Good/Fresh`.

## 7. Проверки

- `dotnet restore Dispatcher.slnx --locked-mode` — успешно, все проекты актуальны;
- `dotnet build Dispatcher.slnx --configuration Release --no-restore` — успешно, 0 warnings, 0 errors;
- Unit — 147/147 успешно;
- целевые SNMP/protocol Unit — 16/16 успешно;
- loopback SNMP UDP Integration — 4/4 успешно;
- production SNMP current/history/alarm/event acceptance — 1/1 успешно;
- объединённый SNMP/Modbus/commissioning/reconciliation regression — 18/18 успешно.

## 8. Ограничения

- Physical device acceptance выполняется в C15.
- SNMP v3, SET, GETBULK, walk, traps и informs отсутствуют.
- Durable diagnostic jobs и Engineering UI относятся к C14.

## 9. Итог

Критерии C13 выполнены. SNMP v2c read-only подключён к production RuntimeHost configuration reconciliation, общему scheduler и durable pipeline; secret boundary, wire limits, timeout/retry, recovery и generation fencing проверены без Docker, Linux и физической записи.
