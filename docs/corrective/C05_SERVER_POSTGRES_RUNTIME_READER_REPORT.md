# C05 — Server PostgreSQL runtime reader: итоговый отчёт

## 1. Результат

Server переведён с process-local `RuntimeRegistry` на асинхронный `CoreRuntimePublishedReader`. HTTP snapshot, runtime readiness, SignalR bootstrap/delta, equipment current projection и Simulator command evidence читают только опубликованный Core current contract из PostgreSQL.

Отдельно запущенная runtime composition становится видима Server после durable publication без общей памяти и ручной регистрации. При отсутствии полной current-конфигурации runtime endpoints отвечают fail-closed и не создают in-memory fallback.

## 2. Production registration

`AddDispatcherServer` регистрирует PostgreSQL reader только при наличии всех настроек:

- `ConnectionStrings:Dispatcher`;
- `Dispatcher:Core:PublishedReadRole`;
- `Dispatcher:Core:MaxSnapshotPoints`;
- `Dispatcher:Core:MaxDeltaChanges`.

Reader использует отдельную published-read role из migration C03. Server не получает Core writer role и не применяет migrations при старте.

Если хотя бы одна настройка отсутствует или limit не положительный, Server продолжает запуск остальных capabilities, но runtime snapshot/readiness/SignalR bootstrap возвращают `runtime.current_unavailable`.

## 3. Bounded published reads

`CoreRuntimePublishedReader` получил обязательный `PublishedCurrentReadLimits`.

- Snapshot читает не более `MaxSnapshotPoints + 1`; превышение возвращается Server как `runtime.query_limit_exceeded`, частичный snapshot не выдаётся.
- Delta читает bounded page `MaxDeltaChanges + 1`.
- Если изменений больше лимита, private Core cursor продвигается только до последней фактически прочитанной записи. Следующий poll продолжает с неё без пропуска.
- Cursor too old и cursor ahead сохраняют отдельную gap-семантику.
- Snapshot/readiness и delta по-прежнему используют `RepeatableRead`.

Limits задаются deployment-конфигурацией и не управляются Web payload.

## 4. Authorization и realtime

`AuthorizedRuntimeReader` стал полностью асинхронным.

- Session authorization выполняется до PostgreSQL read.
- Point filtering выполняется в Server после authorization.
- Published entry преобразуется в существующий transport DTO без recovery/delivery полей.
- Hidden changes продвигают private Core cursor, но возвращают `NoChange` и не меняют Web cursor.
- Retention gap, неверный Web cursor и runtime read failure очищают subscription и требуют bootstrap.
- Замена session, revoke/expiry, изменение grants/denials или visible point set инвалидируют subscription.
- Explicit `BootstrapPoints` сохраняет запрошенное подмножество; обычный bootstrap отслеживает изменение полного видимого point set.

Добавлен endpoint:

`GET /api/runtime/{scopeId}/readiness`

Он возвращает только published/ready/can-serve state, safe degradation code и timestamps. Obligation position, internal delivery state и private cursor наружу не выдаются.

## 5. Удаление общей памяти

`RuntimeRegistry` удалён из Server production composition и implementation.

Зависевшие от него consumers переведены на published current:

- runtime HTTP и SignalR;
- equipment detail current projection;
- Simulator command context/prepare/execute evidence.

Command compatibility adapter читает только целевой point из authorized published snapshot. Protocol I/O и physical command path не добавлялись.

## 6. PostgreSQL role boundary

Integration composition использует разные роли:

- writer — `dispatcher_test_owner_b`;
- Server published reader — `dispatcher_test_owner_a`.

Проверено, что Server видит durable publication отдельной writer composition. Существующие C03 security tests подтверждают, что published-read role:

- читает только `published_scope`, `published_current`, `published_delta`;
- не читает checkpoint, source obligations и processing delivery;
- не может изменять published current.

## 7. Проверенные отказные сценарии

- runtime configuration отсутствует — HTTP snapshot отвечает `503`, fallback отсутствует;
- scope не опубликован — `runtime.scope_not_found`;
- published scope не ready/protected — `runtime.scope_not_ready`;
- snapshot превышает cap — частичные данные не выдаются;
- delta больше cap — выдаётся несколькими страницами без cursor skip;
- cursor вытеснен retention — SignalR `Gap` и новый bootstrap;
- hidden-only delta — `NoChange`, Web cursor не продвигается;
- point permission removed — subscription инвалидируется и client state очищается;
- неверный Web cursor и reconnect требуют bootstrap;
- PostgreSQL read failure не раскрывает connection string или role.

## 8. Изменённые файлы

### Core

- `src/Dispatcher.Core/CoreRuntimePublishedReader.cs`
- `src/Dispatcher.Core/PublishedCurrentModels.cs`

### Server

- `src/Dispatcher.Server/ServerComposition.cs`
- `src/Dispatcher.Server/RuntimeAccess.cs`
- `src/Dispatcher.Server/RuntimeContracts.cs`
- `src/Dispatcher.Server/RuntimeRealtimeHub.cs`
- `src/Dispatcher.Server/RegistryProjections.cs`
- `src/Dispatcher.Server/CommandEndpoints.cs`

### Integration tests

- `tests/Dispatcher.IntegrationTests/ServerRealtimeTests.cs`
- `tests/Dispatcher.IntegrationTests/CoreRuntimePublishedReadTests.cs`
- `tests/Dispatcher.IntegrationTests/CoreRuntimePublicationSecurityCleanupTests.cs`
- `tests/Dispatcher.IntegrationTests/RuntimePipelineFaultRecoveryTests.cs`
- `tests/Dispatcher.IntegrationTests/RegistryProjectionTests.cs`
- `tests/Dispatcher.IntegrationTests/ProtocolCommissioningAcceptanceTests.cs`

### Restore baseline

- `tests/Dispatcher.UnitTests/packages.lock.json`
- `tests/Dispatcher.IntegrationTests/packages.lock.json`

Оба lock-файла были stale относительно уже существующей project dependency `Dispatcher.RuntimeHost`. Они обновлены через `dotnet restore --force-evaluate`, после чего обязательный locked restore прошёл.

## 9. Проверки

Выполнены:

```powershell
dotnet restore Dispatcher.slnx --locked-mode
dotnet build Dispatcher.slnx -c Release --no-restore
dotnet test tests\Dispatcher.UnitTests\Dispatcher.UnitTests.csproj -c Release --no-build --no-restore
dotnet test tests\Dispatcher.IntegrationTests\Dispatcher.IntegrationTests.csproj -c Release --no-build --no-restore
```

Результат:

- locked restore успешен;
- Release build: 0 предупреждений, 0 ошибок;
- Unit: 108 успешно, 0 сбоев, 0 пропущено;
- Integration: 118 успешно, 0 сбоев, 0 пропущено;
- всего: 226 тестов успешно.

Дополнительно целевыми filters проверены Server realtime, bounded published reader, role security, registry projection и protocol commissioning acceptance.

Использовался отдельный временный PostgreSQL cluster. Пользовательская локальная database не изменялась. Docker и Linux не использовались.
