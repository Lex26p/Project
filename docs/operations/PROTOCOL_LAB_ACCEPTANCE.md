# Protocol lab acceptance

## Scope

Windows x64, read-only production adapters:

- Wiren Board 8.5, Modbus TCP;
- Delta single-phase UPS, SNMP v2c.

Credentials and network addresses are supplied only through local environment
variables and are not stored in this report.

## Safety boundary

- Modbus requests are limited to FC03/FC04.
- SNMP requests are limited to GET.
- No physical command, Modbus write function or SNMP SET is exposed by the
  acceptance harness.

## Reproduction

Set the following local environment variables:

- `DISPATCHER_C15_RUN=1`
- `DISPATCHER_C15_MODBUS_HOST`
- optional `DISPATCHER_C15_MODBUS_PORT` (default `502`)
- optional `DISPATCHER_C15_MODBUS_UNAVAILABLE_PORT`
- `DISPATCHER_C15_SNMP_HOST`
- optional `DISPATCHER_C15_SNMP_PORT` (default `161`)
- optional `DISPATCHER_C15_SNMP_UNAVAILABLE_PORT` (default `65000`)
- `DISPATCHER_C15_SNMP_COMMUNITY`

Run:

```powershell
dotnet test tests\Dispatcher.IntegrationTests\Dispatcher.IntegrationTests.csproj `
  --configuration Release `
  --filter "Category=PhysicalLab"
```

## Recorded evidence

### Happy path

- Modbus TCP on the recovered endpoint: input and holding registers returned
  six Good samples; signed,
  unsigned, byte-order, word-order and fractional `0.1` scale mappings passed.
- SNMP v2c: all five configured OIDs returned Good samples; voltage `0.1`
  scales and normal UPS status mapping passed.
- Result: 2 tests passed, 0 failed.
- Observed happy-path test durations: Modbus 233 ms; SNMP 253 ms.

### Disconnect and recovery

- Modbus endpoint disconnect confirmed: the former port returned a bounded
  `protocol.io_timeout`/`protocol.io_failed` result within the configured
  retry window (4-second acceptance test duration).
- Modbus recovery confirmed the sequence Good/Fresh → Bad/Stale → Good/Fresh.
  The recovered current was accepted with a new source session generation.
- SNMP recovery confirmed the same sequence by locally routing the unavailable
  phase to an unused UDP port. UPS state and configuration were not changed.
- Both recovery scenarios completed inside their bounded 4-second unavailable
  windows.

### Runtime pipeline and wire surface

- Production RuntimeHost regression passed for Modbus and SNMP:
  current, History, Alarm and Event delivery completed after protocol polling.
- Modbus peer evidence contained only FC03.
- The configured hardware surface contains only FC03/FC04 reads.
- SNMP peer evidence contained only GET PDU `0xA0`.
- Protocol regression: 21 focused Modbus/SNMP tests passed.

## Conclusion

C15 read-only hardware acceptance is complete. Both adapters passed happy path,
bounded unavailability, Bad/Stale propagation, recovery and session fencing.
No physical write or UPS fault was generated.
