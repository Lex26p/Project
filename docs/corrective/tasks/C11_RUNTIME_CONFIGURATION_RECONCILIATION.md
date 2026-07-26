# C11 — Workload configuration reconciliation и activation

## 1. Назначение

Заменить ручной Simulator bootstrap полноценным durable flow опубликованной конфигурации в RuntimeHost.

## 2. Архитектурный контекст

Прочитать:

- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 7, 9 и 13;
- `docs/ADR-004_POSTGRESQL_PERSISTENCE.md`;
- `WEB_BACKEND_API_REQUIREMENTS.md`, разделы 4 и 5.4;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- `src/Dispatcher.Configuration/ConfigurationService.cs`
- `src/Dispatcher.Configuration/ConfigurationStore.cs`
- `src/Dispatcher.Configuration/ConfigurationModels.cs`
- `src/Dispatcher.Configuration/ConfigurationMigrations.cs`
- `src/Dispatcher.Server/SimulatorReleaseActivator.cs`
- `src/Dispatcher.ProtocolCommissioning/ProtocolCommissioningManifest.cs`
- `src/Dispatcher.ProtocolCommissioning/ProtocolDeploymentContinuity.cs`
- `src/Dispatcher.Simulator/SimulatorRuntimeStore.cs`
- RuntimeHost worker/source composition C02–C04
- `tests/Dispatcher.IntegrationTests/ConfigurationRevisionTests.cs`
- `tests/Dispatcher.IntegrationTests/SimulatorActivationTests.cs`
- `tests/Dispatcher.UnitTests/ProtocolCommissioningTests.cs`

Остальные файлы изучать по необходимости.

## 4. Целевой результат

RuntimeHost под workload identity:

1. находит/claims следующую distributed scope revision;
2. проверяет manifest и fingerprints;
3. строит целый activation plan;
4. подготавливает Simulator и Alarm definition epoch;
5. выдаёт новые binding/session generations;
6. атомарно переключает active runtime generation после успешной подготовки;
7. подтверждает activation outcome;
8. при crash повторяет flow без двойного переключения.

Invalid revision не отключает последнюю рабочую generation.

## 5. Объём реализации

- В Configuration owner создать явный workload deployment contract. Не вызывать `ConfigurationService` с фиктивной user session.
- Использовать существующие distribution lease/version/fingerprint механизмы либо расширить их новой migration version.
- Перенести ответственность `SimulatorReleaseActivator` из Server user flow в корректный workload flow; удалить или переопределить старый неиспользуемый путь.
- Ввести bounded reconciliation loop RuntimeHost.
- Реализовать prepare/commit/ack semantics whole-scope activation.
- Fencing старых in-flight results.
- Активировать Alarm definition set точной revision; разрешить пустой набор.
- Оставить extension points для Modbus/SNMP C12/C13 без их I/O.
- Сохранить sanitised rejection/result для инженерного UI.

## 6. Архитектурные требования

- User publish и workload activation — разные authority boundaries.
- Ack выполняется только после реальной runtime activation.
- Partial source activation не становится active scope revision.
- Secret values не входят в manifest result/audit.
- Старый source drain и новый source start имеют deterministic order.
- RuntimeHost не вызывает Server по HTTP.

## 7. Обязательные fault tests

- Crash после claim.
- Crash после prepare, до switch.
- Crash после switch, до acknowledgement.
- Invalid fingerprint/manifest.
- New revision rejected while old stays active.
- Stale worker lease.
- Stale poll from old binding after successful switch.

## 8. Критерии приёмки

- Новая Simulator revision начинает выдавать новые значения без restart процесса.
- Active revision и runtime generation согласованы.
- Повтор после crash не дублирует activation.
- Invalid revision оставляет старую generation работающей.
- Alarm epoch меняется только вместе с runtime generation.
- Existing configuration/simulator tests проходят.

## 9. За пределами задания

- Modbus/SNMP source factories.
- Engineering Web.
- Physical device I/O.

## 10. Итоговый отчёт

Описать workload authorization, prepare/switch/ack boundary и recovery cases.

