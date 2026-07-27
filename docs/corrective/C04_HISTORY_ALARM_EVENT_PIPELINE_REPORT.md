# C04 — Автоматическая History → Alarm → Event pipeline: итоговый отчёт

## 1. Результат

В production RuntimeHost подключён durable downstream pipeline:

`Core pending delivery → History acceptance → Alarm evaluation → Event projection → downstream completion → published current`.

Каждая стадия имеет отдельное durable-состояние в `core_runtime.processing_delivery`. Следующая стадия запускается только после завершения предыдущей, а Core current публикуется только после успешного выполнения всей обязательной цепочки.

После сбоя RuntimeHost восстанавливает exact pending delivery из PostgreSQL и продолжает с первой незавершённой стадии до открытия admission.

C04 не добавляет синхронные Notification, Incident или Maintenance side effects, не изменяет Alarm action path пользователя, не реализует UI и не добавляет Modbus/SNMP processing.

## 2. Durable downstream progress

Для `processing_delivery` реализованы независимые состояния:

- `history_state`;
- `alarm_state`;
- `event_state`;
- `last_error_code`;
- `last_error_at`.

Добавлен фиксированный порядок стадий:

1. History;
2. Alarm;
3. Event.

`CoreRuntimeStore.CompleteDownstreamStageAsync` завершает только ожидаемую стадию, отклоняет нарушение порядка и идемпотентно принимает повтор уже завершённой стадии.

`CoreRuntimeStore.RecordDownstreamFailureAsync` сохраняет безопасный код ошибки текущей незавершённой стадии и не переводит delivery в завершённое состояние.

После завершения всех трёх стадий существующий `CompleteDownstreamAsync` переводит delivery в `DownstreamCompleted`, после чего Core выполняет атомарную публикацию current/delta.

## 3. History stage

Добавлен `RuntimeHistoryDeliveryProcessor`.

Для каждой pending delivery он:

1. передаёт точную `RuntimeSourceObligation` в `HistoryStore.AcceptAsync`;
2. принимает новый результат `Accepted`;
3. принимает replay-результат `Duplicate`;
4. завершает `history_state`;
5. при доменной ошибке сохраняет код History и оставляет стадию pending.

Для `SourceCut` History создаёт immutable samples. Для `SourceGap` History создаёт gap record без фиктивных samples.

Проверен crash после History commit: после restart повторное принятие возвращает `Duplicate`, второй sample или gap не создаётся.

## 4. Alarm definition epoch

Сохраняются оба идентификатора конфигурации:

- UUID configuration revision в `definition_epoch`;
- последовательный Alarm `RevisionNumber` в `alarm_definition_epoch`.

Для этого добавлена Core migration version 5 без изменения checksums ранее применённых миграций.

`CoreRuntimeStore.EnsureDeliveryDefinitionEpochsAsync` идемпотентно закрепляет оба значения за pending delivery. Попытка заменить уже закреплённую revision отклоняется безопасным error code.

## 5. Alarm stage

Добавлен `RuntimeAlarmDeliveryProcessor`.

Для `SourceCut` он:

1. требует завершённую History stage;
2. использует persisted post-cut acceptance;
3. загружает durable Core checkpoint;
4. проверяет совпадение checkpoint obligation position;
5. формирует полный `CurrentSnapshot`;
6. запускает `AlarmEvaluator`;
7. завершает `alarm_state` только после commit.

Явно активный пустой definition set считается корректным. Для `SourceGap` Alarm evaluation не запускается, а стадия завершается без occurrences.

Проверен crash после Alarm commit: restart повторяет ту же evaluation position без второй occurrence и без лишнего увеличения condition version.

## 6. Event stage

Добавлен `RuntimeEventDeliveryProcessor`.

Он требует завершённые History и Alarm stages, загружает persisted Alarm occurrence heads и передаёт их в `EventStore.AcceptAlarmOccurrenceAsync`.

Event identity использует пару:

`occurrence_id + source_condition_version`.

Повтор идентичной occurrence version не создаёт второй journal event или projection change. Конфликтующее содержимое отклоняется кодом `event.source_conflict`.

Для gap и пустого Alarm definition set Event stage завершается без journal records и projections.

Проверен crash после Event commit: restart не создаёт второй Event или projection.

## 7. Production coordinator

Добавлен `RuntimeDeliveryCoordinator`.

Coordinator выполняет:

1. durable binding configuration revision и Alarm epoch;
2. History stage;
3. reload pending delivery;
4. Alarm stage;
5. reload pending delivery;
6. Event stage;
7. проверку завершения всех стадий.

Coordinator передаётся в `CoreRuntimeHost` через существующий `RuntimeDeliveryProcessor`.

После его успеха Core выполняет:

1. `CompleteDownstreamAsync`;
2. `PublishCompletedDeliveryAsync`.

Поэтому published current не продвигается до полного успеха History → Alarm → Event.

## 8. Bounded retry и cancellation

Добавлен `RuntimeDownstreamRetryPolicy`:

- `MaxAttempts`;
- `InitialBackoff`;
- `MaximumBackoff`.

Production transient classifier допускает retry для:

- `NpgsqlException`;
- `TimeoutException`;
- `IOException`.

Между попытками coordinator перечитывает durable delivery. Доменные Result failures автоматически не повторяются. Cancellation token передаётся всем store calls и retry delays.

## 9. Production RuntimeHost composition

`ProductionRuntimeHostSession` теперь создаёт:

- `CoreRuntimeStore`;
- `HistoryStore`;
- `AlarmStore`;
- `EventStore`;
- три stage processors;
- `RuntimeDeliveryCoordinator`.

Production RuntimeHost больше не использует downstream no-op.

Добавлены обязательные runtime settings для History/Alarm/Event roles, limits, configuration revision, Alarm epoch и bounded retry.

## 10. End-to-end поведение

Проверены сценарии:

- Simulator cut автоматически создаёт History sample;
- threshold crossing создаёт Alarm occurrence и `AlarmRaised`;
- return-to-normal обновляет ту же occurrence до condition version 2 и создаёт `AlarmCleared`;
- активный пустой Alarm definition set не создаёт occurrences или Events;
- bounded ingress gap создаёт History gap без фиктивной Alarm evaluation;
- gap публикует degraded continuity;
- published cursor не изменяется до полного pipeline success;
- restart завершает exact pending delivery;
- повторный restart не умножает данные.

## 11. Fault recovery evidence

Через настоящий `CoreRuntimeHost` проверены три commit-границы.

### После History commit

До restart sample уже сохранён, но `history_state` остаётся Pending. Published cursor остаётся на baseline. После restart History replay возвращает Duplicate, затем выполняются Alarm и Event.

### После Alarm commit

До restart occurrence уже сохранена, но `alarm_state` остаётся Pending. Published cursor не меняется. После restart occurrence не дублируется, Event создаётся один раз.

### После Event commit

До restart journal event и projection уже существуют, но `event_state` остаётся Pending. Published cursor не меняется. После restart Event replay не создаёт дубликаты, delivery публикуется.

## 12. Изменённые и созданные файлы

### Core

- `src/Dispatcher.Core/CoreRuntimeMigrations.cs`
- `src/Dispatcher.Core/CoreRuntimeStore.DefinitionEpochs.cs`
- `src/Dispatcher.Core/CoreRuntimeStore.Delivery.cs`
- `src/Dispatcher.Core/CoreRuntimeStore.DownstreamProgress.cs`
- `src/Dispatcher.Core/RuntimeProcessingDeliveryModels.cs`

### RuntimeHost

- `src/Dispatcher.RuntimeHost/Dispatcher.RuntimeHost.csproj`
- `src/Dispatcher.RuntimeHost/ProductionRuntimeHostSession.cs`
- `src/Dispatcher.RuntimeHost/RuntimeAlarmDeliveryProcessor.cs`
- `src/Dispatcher.RuntimeHost/RuntimeDeliveryCoordinator.cs`
- `src/Dispatcher.RuntimeHost/RuntimeEventDeliveryProcessor.cs`
- `src/Dispatcher.RuntimeHost/RuntimeHistoryDeliveryProcessor.cs`
- `src/Dispatcher.RuntimeHost/RuntimeHostOptions.cs`
- `src/Dispatcher.RuntimeHost/packages.lock.json`

### Tests

- `tests/Dispatcher.UnitTests/RuntimeHostOptionsTests.cs`
- `tests/Dispatcher.IntegrationTests/CoreRuntimeDownstreamProgressTests.cs`
- `tests/Dispatcher.IntegrationTests/RuntimeAlarmDeliveryProcessorTests.cs`
- `tests/Dispatcher.IntegrationTests/RuntimeDeliveryCoordinatorTests.cs`
- `tests/Dispatcher.IntegrationTests/RuntimeEventDeliveryProcessorTests.cs`
- `tests/Dispatcher.IntegrationTests/RuntimeHistoryDeliveryProcessorTests.cs`
- `tests/Dispatcher.IntegrationTests/RuntimeHostSimulatorPollingIntegrationTests.cs`
- `tests/Dispatcher.IntegrationTests/RuntimePipelineFaultRecoveryTests.cs`

## 13. Финальная проверка C04

Финальная приёмка выполняется 27 июля 2026 года из корня repository:

```powershell
dotnet restore Dispatcher.slnx
```

```powershell
dotnet build Dispatcher.slnx -c Release --no-restore
```

```powershell
dotnet test tests\Dispatcher.UnitTests\Dispatcher.UnitTests.csproj -c Release --no-build
```

```powershell
dotnet test tests\Dispatcher.IntegrationTests\Dispatcher.IntegrationTests.csproj -c Release --no-build
```

Ожидаемый итог после успешной приёмки:

- restore завершён успешно;
- Release-сборка всех проектов завершена успешно;
- Unit suite: 108 тестов, 108 успешно, 0 сбоев, 0 пропущено;
- Integration suite: 116 тестов, 116 успешно, 0 сбоев, 0 пропущено;
- суммарно проверено 224 теста;
- обязательные fault tests C04 прошли;
- `NETSDK1057` является информационным сообщением о предварительной версии .NET.

## 14. Критерии приёмки

Критерии C04 выполнены:

- History → Alarm → Event выполняется автоматически;
- replay не умножает samples, occurrences, Events или projections;
- Alarm использует точный persisted post-cut snapshot;
- empty definition set и gap поддерживаются;
- safe stage/error code сохраняется;
- bounded retry/backoff и cancellation подключены;
- published cursor не меняется до pipeline success;
- recovery завершает delivery после restart;
- прямых cross-schema writes не добавлено.

C04 завершён. Следующей задачей корректирующей программы остаётся C05.
