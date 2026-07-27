# C03 — Durable processing delivery и published current: итоговый отчёт

## 1. Результат

В `Dispatcher.Core` реализована Core-owned durable граница между применением runtime fact, downstream processing и публикацией current:

`protected obligation → apply/checkpoint + pending delivery → downstream acknowledgement → publish current/delta + delivery complete`.

После сбоя между стадиями RuntimeHost восстанавливает точную незавершённую delivery до открытия admission. Server-facing published state хранится отдельно от recovery internals и доступен через отдельный read contract.

C03 не реализует реальные History/Alarm/Event processors, Server endpoints, SignalR/Web integration или protocol activation. Для downstream processing добавлена тестируемая граница, которая сейчас использует no-op processor.

## 2. Durable schema

В `core_runtime` добавлены migration versions 3 и 4.

Migration version 3 создаёт:

- `core_runtime.processing_delivery`;
- `core_runtime.published_scope`;
- `core_runtime.published_current`;
- `core_runtime.published_delta`;
- ограничения lifecycle и индексы.

`processing_delivery` содержит:

- identity scope/obligation;
- тип runtime fact;
- durable stage;
- сериализованный post-cut acceptance либо gap reason;
- состояния будущих History/Alarm/Event consumers;
- safe last error code;
- timestamps downstream completion и publication.

Для одного scope допускается не более одной unfinished delivery.

`published_scope` хранит:

- последнюю завершённую obligation position;
- current cursor;
- earliest retained delta position;
- protected continuity;
- readiness;
- degradation reason;
- heartbeat и publication timestamps.

`published_current` хранит последнее опубликованное состояние point, а `published_delta` — ограниченный журнал изменений.

Migration version 4 создаёт границу PostgreSQL published-read role.

## 3. Transaction boundary: checkpoint и pending delivery

`CoreRuntimeStore.SaveCheckpointWithPendingDeliveryAsync` выполняет одной PostgreSQL-транзакцией:

1. блокировку scope;
2. проверку отсутствия другой unfinished delivery;
3. обновление durable checkpoint;
4. обновление checkpoint obligation position и protected continuity;
5. отметку obligation как checkpointed;
6. вставку `processing_delivery` в stage `Pending`;
7. сохранение достаточного post-cut acceptance для downstream replay.

Следующая obligation не может получить собственную pending delivery, пока предыдущая не завершена.

Checkpoint без соответствующей pending delivery в production processing path не публикует Server-facing current.

## 4. Transaction boundary: downstream completion

`CoreRuntimeStore.CompleteDownstreamAsync` переводит delivery из `Pending` в `DownstreamCompleted`.

Переход допускается только для ожидаемой unfinished delivery. В той же транзакции фиксируются acknowledgement states будущих downstream consumers и `downstream_completed_at`.

Повтор того же completion идемпотентен. Попытка завершить другую delivery или нарушить порядок отклоняется.

## 5. Transaction boundary: publication

`CoreRuntimeStore.PublishCompletedDeliveryAsync` одной PostgreSQL-транзакцией:

1. блокирует scope и delivery;
2. проверяет stage `DownstreamCompleted`;
3. проверяет ordering относительно `published_scope.completed_obligation_position`;
4. применяет current transitions в `published_current`;
5. добавляет изменения в `published_delta`;
6. обновляет cursor, readiness и protected continuity в `published_scope`;
7. удаляет delta старше configured retention capacity;
8. переводит delivery в stage `Published`;
9. фиксирует `published_at`.

Публикация current и завершение delivery атомарны. Состояние, где current уже опубликован, но delivery остаётся unfinished, не создаётся.

Повторная finalize той же delivery возвращает идемпотентный результат без дублирования current или delta. Finalize не по порядку отклоняется.

## 6. RuntimeHost processing и recovery

`CoreRuntimeHost` получил rich processing outcome:

- `RuntimeProcessNextStatus.Idle`;
- `RuntimeProcessNextStatus.Published`;
- обработанную obligation;
- результат publication commit.

Существующий `ProcessNextAsync` сохранён как совместимая bool-обёртка.

Production processing path:

1. durable append protected obligation;
2. apply runtime fact;
3. atomic checkpoint + pending delivery;
4. вызов `RuntimeDeliveryProcessor`;
5. durable downstream completion;
6. atomic current/delta publication;
7. переход к следующей obligation.

Во время startup RuntimeHost:

1. закрывает admission;
2. загружает checkpoint и protected continuity;
3. восстанавливает `CoreRuntime`;
4. загружает exact pending delivery;
5. завершает её downstream processing/publication;
6. replay-ит оставшиеся protected obligations последовательно;
7. только после полного recovery открывает admission.

После уже принятого runtime cut cancellation polling worker останавливает новые polls, но не прерывает bounded durable processing этой obligation. Это не позволяет cancellation оставить host в faulted state после зафиксированного checkpoint.

## 7. Fault recovery

Проверены следующие точки сбоя:

### После append obligation, до apply

Существующий recovery contract загружает protected obligation после checkpoint и последовательно replay-ит её.

### После checkpoint и pending delivery

После restart загружается exact pending delivery с post-cut acceptance. Она завершается до открытия admission и публикуется ровно один раз.

### После downstream completion

После restart delivery восстанавливается в stage `DownstreamCompleted`. Publication выполняется без повторного downstream acknowledgement.

### После publication commit

Publication уже содержит current/delta и delivery stage `Published`. После restart pending delivery отсутствует, snapshot восстанавливается из checkpoint, а current, delta и число published deliveries не дублируются.

### Повтор finalize

Повторная publication той же identity возвращает идемпотентный результат.

### Нарушение порядка

Попытка опубликовать delivery не по ожидаемой obligation position отклоняется.

## 8. Published read contract

Добавлен отдельный `CoreRuntimePublishedReader`.

Он предоставляет:

- `ReadReadinessAsync`;
- `ReadSnapshotAsync`;
- `ReadDeltaAsync`.

Reader не содержит write, checkpoint, obligation или processing-delivery API.

Readiness и соответствующий snapshot/delta читаются в PostgreSQL-транзакции `RepeatableRead`, поэтому Server не получает cursor из одного publication commit и entries из другого.

`PublishedRuntimeReadiness` содержит:

- признак существования published scope;
- completed obligation position;
- current cursor;
- earliest resumable cursor;
- protected continuity;
- readiness;
- degradation reason;
- heartbeat и publication timestamps.

Delta result различает:

- `Available`;
- `ScopeNotPublished`;
- `CursorTooOld`;
- `CursorAhead`.

Правило cursor retention:

`earliest resumable cursor = earliest retained delta position - 1`.

Если cursor старше этого значения, consumer должен получить новый snapshot. Cursor впереди current возвращается отдельным ошибочным состоянием. Delta pruning не изменяет `published_current`.

## 9. Published-read PostgreSQL role

Для DatabaseMigrator добавлена обязательная настройка:

`DISPATCHER_MIGRATIONS_ROLE__core_runtime_published_read`.

Migration preflight проверяет:

- существование роли;
- корректность PostgreSQL identifier;
- возможность migration principal выполнить `SET ROLE`.

Выделенная published-read role получает:

- `USAGE` на schema `core_runtime`;
- `SELECT` на `published_scope`;
- `SELECT` на `published_current`;
- `SELECT` на `published_delta`.

Она не получает доступ к:

- `scope_state`;
- `source_obligation`;
- `processing_delivery`;
- `source_session_generation`;
- migration history;
- операциям `INSERT`, `UPDATE` или `DELETE`.

Для совместимых integration-test plans, где owner и read-role намеренно совпадают, migration version 4 использует no-op и не отбирает права у schema owner. Production migrator требует отдельное именованное mapping.

## 10. Bounded cleanup

Добавлен `CoreRuntimeStore.CleanupCompletedDeliveriesAsync`.

Cleanup выполняется ограниченным batch и удаляет только пары:

- delivery имеет stage `Published`;
- publication старше safety window;
- obligation имеет durable checkpoint;
- obligation position не находится впереди checkpoint scope.

Одна транзакция удаляет completed `processing_delivery` и соответствующую `source_obligation`.

Cleanup не удаляет:

- unfinished delivery;
- pending/downstream-completed delivery;
- obligation без checkpoint;
- recovery-required запись;
- published current;
- retained delta.

`maxDeleteCount` ограничивает объём одной cleanup-транзакции.

## 11. Изменённые и созданные файлы

### Core

- `src/Dispatcher.Core/CoreRuntimeHost.cs`
- `src/Dispatcher.Core/CoreRuntimeMigrations.cs`
- `src/Dispatcher.Core/CoreRuntimePublishedReader.cs`
- `src/Dispatcher.Core/CoreRuntimeStore.Cleanup.cs`
- `src/Dispatcher.Core/CoreRuntimeStore.Delivery.cs`
- `src/Dispatcher.Core/CoreRuntimeStore.Publication.cs`
- `src/Dispatcher.Core/PublishedCurrentModels.cs`
- `src/Dispatcher.Core/RuntimeProcessingDeliveryModels.cs`

### DatabaseMigrator

- `src/Dispatcher.DatabaseMigrator/DatabaseMigrationCoordinator.cs`
- `src/Dispatcher.DatabaseMigrator/MigrationConfigurationParser.cs`

### RuntimeHost

- `src/Dispatcher.RuntimeHost/SimulatorPollingWorker.cs`

### Integration tests

- `tests/Dispatcher.IntegrationTests/CoreRuntimeDurableDeliveryRecoveryTests.cs`
- `tests/Dispatcher.IntegrationTests/CoreRuntimePendingDeliveryTests.cs`
- `tests/Dispatcher.IntegrationTests/CoreRuntimePublicationCommitTests.cs`
- `tests/Dispatcher.IntegrationTests/CoreRuntimePublicationMigrationTests.cs`
- `tests/Dispatcher.IntegrationTests/CoreRuntimePublicationSecurityCleanupTests.cs`
- `tests/Dispatcher.IntegrationTests/CoreRuntimePublishedReadTests.cs`
- `tests/Dispatcher.IntegrationTests/DatabaseMigratorProductionTests.cs`
- `tests/Dispatcher.IntegrationTests/RuntimeHostSimulatorPollingIntegrationTests.cs`

## 12. Автоматически проверенные сценарии

Проверены:

- migration создаёт durable delivery и published schema;
- migration повторно применяется безопасно;
- checkpoint и pending delivery фиксируются атомарно;
- next checkpoint блокируется unfinished delivery;
- post-cut acceptance восстанавливается без потерь;
- downstream completion идемпотентен;
- publication current/delta и delivery completion атомарны;
- повтор publication не создаёт дубликаты;
- publication не по порядку отклоняется;
- restart после checkpoint/pending завершает delivery;
- restart после downstream completion выполняет только publication;
- restart после publication commit не дублирует published result;
- admission открывается только после recovery;
- production Simulator worker сохраняет current и корректно останавливается;
- snapshot и cursor читаются согласованно;
- cursor too old требует snapshot;
- cursor ahead возвращается отдельно;
- delta pruning не меняет snapshot;
- published storage сохраняет configured capacity;
- read-role видит только published tables;
- read-role не читает recovery internals;
- read-role не изменяет published current;
- migrator требует published-read role mapping;
- cleanup ограничен batch size;
- cleanup сохраняет unfinished и recovery-required записи;
- cleanup не изменяет published snapshot.

## 13. Финальная проверка C03

Финальная приёмка выполнена 27 июля 2026 года из корня repository:

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

Фактический результат:

- restore завершён успешно;
- Release-сборка всех проектов завершена успешно;
- Unit suite: 106 тестов, 106 успешно, 0 сбоев, 0 пропущено;
- Integration suite: 94 теста, 94 успешно, 0 сбоев, 0 пропущено;
- суммарно проверено 200 тестов;
- обязательные fault tests C03 прошли;
- production DatabaseMigrator tests прошли после добавления нового обязательного test mapping;
- информационное сообщение `NETSDK1057` о предварительной версии .NET не является ошибкой сборки.

C03 переведён в `Complete`. Непосредственные зависимости C04 и C05 переведены в `Ready`.
