# C09 — Диспетчер событий, Alarm actions и History workspace: итоговый отчёт

## 1. Результат

Реализованы рабочие маршруты `/events` и `/history`.

Event Dispatcher поддерживает:

- realtime и history views;
- единые filter semantics для списка и counters;
- отдельные condition, acknowledgement, assignment, shelving и suppression facets;
- detail/context panel, timeline и доступные deep links;
- acknowledge, assign/reassign, shelve и unshelve с server authorization, optimistic concurrency, idempotency и audit;
- snapshot/feed cursor, дедупликацию projection updates и server resnapshot после gap.

History workspace поддерживает:

- выбор scope/source/point и временного диапазона;
- raw и aggregate resolution;
- quality, freshness и явные data gaps;
- стабильную raw pagination;
- строго read-only поведение без lifecycle actions.

Bulk action не имитируется, поскольку отдельный bulk API отсутствует.

## 2. Server и Alarm

- Расширен occurrence snapshot payload: все независимые facets, их версии и timestamps, action permissions и deep-link context.
- Добавлены REST snapshot/feed endpoints с монотонным cursor и явным `Gap`.
- Realtime payload формируется с учётом текущей session и эффективных permissions.
- Добавлен `unshelve` action и новая версия Alarm migration.
- Unshelve сохраняет optimistic concurrency, idempotency, audit и projection update.
- `/events` включён в route authorization и capability-filtered navigation.

## 3. Web

- Добавлены `EventApiClient`, Web contracts и `EventDispatcherState`.
- Реализована страница Event Dispatcher с realtime/history режимами, фильтрами, counters, detail panel, timeline и разрешёнными действиями.
- Clearing не изменяет acknowledgement; replay не создаёт duplicate rows; gap вызывает свежий server snapshot.
- Страница History заменена на полноценный read-only workspace с raw/aggregate запросами, quality/freshness badges, trend и gap markers.
- В `HistoryApiClient` добавлен raw range reader со stable pagination.

## 4. Тесты

Добавлены:

- unit tests для согласованности filters/counters, replay deduplication, gap detection и отсутствия implicit acknowledgement;
- integration coverage для полного shelve → unshelve цикла;
- пять browser-сценариев C09:
  - filters/counters;
  - acknowledge, assignment/reassignment, shelving/unshelving;
  - новое occurrence и return-to-normal без acknowledge;
  - realtime gap и resnapshot;
  - History quality/gaps и отсутствие lifecycle actions.

Результаты:

- `dotnet restore Dispatcher.slnx --locked-mode` — успешно;
- `dotnet build Dispatcher.slnx -c Release --no-restore` — успешно, 0 warnings, 0 errors;
- Unit — 139/139 успешно;
- Browser — 14/14 успешно, включая C09 5/5;
- целевые C09 Integration (`EventDispatcherTests`, `AlarmActionTests`, `HistoryAcceptanceTests`) — 15/15 успешно;
- полный Integration — 119/120 в первом прогоне: один независимый `MaintenanceSchedulerTests` завершился transient failure; одиночный повтор того же теста — 1/1 успешно.

## 5. Изменённые области

### Production

- `src/Dispatcher.Alarm/AlarmActions.cs`
- `src/Dispatcher.Alarm/AlarmActionStore.cs`
- `src/Dispatcher.Alarm/AlarmMigrations.cs`
- `src/Dispatcher.Server/AlarmActionEndpoints.cs`
- `src/Dispatcher.Server/EventEndpoints.cs`
- `src/Dispatcher.Server/WorkspaceEndpoints.cs`
- `src/Dispatcher.Web/EventApiClient.cs`
- `src/Dispatcher.Web/EventContracts.cs`
- `src/Dispatcher.Web/EventDispatcherState.cs`
- `src/Dispatcher.Web/HistoryApiClient.cs`
- `src/Dispatcher.Web/HistoryWorkspace.razor`
- `src/Dispatcher.Web/Pages/Events.razor`
- `src/Dispatcher.Web/Pages/History.razor`
- `src/Dispatcher.Web/Program.cs`
- `src/Dispatcher.Web/wwwroot/app.css`

### Tests

- `tests/Dispatcher.UnitTests/EventDispatcherStateTests.cs`
- `tests/Dispatcher.IntegrationTests/AlarmActionTests.cs`
- `tests/Dispatcher.BrowserTests/BrowserScenario.cs`
- `tests/Dispatcher.BrowserTests/C09EventHistoryBrowserTests.cs`

## 6. Итог

Критерии C09 выполнены. Event Dispatcher использует фактические независимые Alarm facets, разрешённые действия проходят через Server, realtime cursor корректно обрабатывает replay и gaps, а History остаётся строго read-only.

