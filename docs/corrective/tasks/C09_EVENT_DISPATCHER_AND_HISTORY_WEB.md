# C09 — Диспетчер событий, Alarm actions и History workspace

## 1. Назначение

Создать основное рабочее место оператора для событий/аварий и довести History до продуктового read-only сценария.

## 2. Архитектурный контекст

Прочитать:

- `WEB_INTERFACE_SPECIFICATION.md`, разделы 9, 10, 17 и 20;
- `WEB_BACKEND_API_REQUIREMENTS.md`, разделы 4, 5.3 и 5.7;
- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 9–12;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- `src/Dispatcher.Server/EventEndpoints.cs`
- `src/Dispatcher.Server/AlarmActionEndpoints.cs`
- `src/Dispatcher.Server/HistoryEndpoints.cs`
- `src/Dispatcher.Events/EventStore.cs`
- `src/Dispatcher.Alarm/AlarmActionStore.cs`
- `src/Dispatcher.History/HistoryStore.cs`
- `src/Dispatcher.Web/Pages/History.razor`
- `src/Dispatcher.Web/HistoryApiClient.cs`
- `src/Dispatcher.Web/HistoryTrendWidget.razor`
- Web shell/components C08
- `tests/Dispatcher.IntegrationTests/EventDispatcherTests.cs`
- `tests/Dispatcher.IntegrationTests/AlarmActionTests.cs`
- `tests/Dispatcher.IntegrationTests/HistoryAcceptanceTests.cs`

Остальные файлы изучать по необходимости.

## 4. Целевой результат

Маршрут Event Dispatcher предоставляет:

- одинаковую filter semantics для counters и списка;
- active/history views;
- независимое отображение condition, acknowledgement, assignment, shelving/suppression;
- stable pagination/cursor;
- detail/context panel и timeline;
- acknowledge, assign/reassign, shelve/unshelve только когда action разрешён;
- realtime feed с gap/resync.

History workspace:

- выбирает points и диапазон;
- показывает quality и gaps;
- поддерживает raw/aggregate resolution в существующих limits;
- остаётся строго read-only.

## 5. Объём реализации

- Расширить существующие Server contracts только там, где стабильное требование ещё не представлено.
- Не дублировать Alarm state в Web.
- Добавить Web clients, state и страницы.
- Сохранить optimistic concurrency/idempotency actions.
- Добавить deep links к equipment/dashboard/history, если target уже существует и разрешён.
- Browser E2E:
  - фильтр/counters;
  - новое occurrence;
  - acknowledge;
  - assignment;
  - return-to-normal без implicit acknowledge;
  - shelving;
  - realtime gap;
  - history gap/quality;
  - запрет actions в History.

## 6. Архитектурные требования

- Event fact не получает неприменимый acknowledgement.
- Clearing не выполняет acknowledgement.
- Shelving не равно suppression.
- Bulk action возвращает per-item result; если bulk API не существует, не имитировать атомарный success на клиенте.
- Permission change инвалидирует action state.
- Исторический экран не выполняет transitions.

## 7. Критерии приёмки

- UI отражает фактические отдельные facets.
- Counters и список согласованы.
- Realtime replay не создаёт duplicate rows.
- Gap приводит к server resnapshot.
- Все actions проходят server authorization и audit.
- Browser и существующие integration tests проходят.

## 8. За пределами задания

- Полный incident workspace.
- Maintenance request creation.
- Notifications.
- Provisional reports/shift journal.

## 9. Итоговый отчёт

Указать реализованные filters/actions, realtime cursor model и browser evidence.

