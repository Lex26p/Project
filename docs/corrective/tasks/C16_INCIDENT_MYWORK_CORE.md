# C16 — Incident и My Work Core API/Web

## 1. Назначение

Подключить уже реализованные Incident и My Work модули к Server/Web в границе стабильных продуктовых сценариев.

## 2. Архитектурный контекст

Прочитать:

- `WEB_INTERFACE_SPECIFICATION.md`, разделы 17.8, 18 и 25.1;
- `WEB_BACKEND_API_REQUIREMENTS.md`, разделы 5.8 и 5.10;
- `docs/DISPATCHER_MASTER_IMPLEMENTATION_SPECIFICATION.md`, scope maturity;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- `src/Dispatcher.Incidents/IncidentModels.cs`
- `src/Dispatcher.Incidents/IncidentStore.cs`
- `src/Dispatcher.Incidents/IncidentService.cs`
- `src/Dispatcher.MyWork/MyWorkModels.cs`
- `src/Dispatcher.MyWork/MyWorkStore.cs`
- `src/Dispatcher.MyWork/MyWorkService.cs`
- `src/Dispatcher.Server/EventEndpoints.cs`
- `src/Dispatcher.Server/Program.cs`
- Event Dispatcher Web C09
- `tests/Dispatcher.IntegrationTests/IncidentMyWorkTests.cs`

Остальные файлы изучать по необходимости.

## 4. Целевой результат

Пользователь может:

- создать отдельный Incident из разрешённого Event/Occurrence;
- получить compact incident summary и перейти по permission-filtered link;
- создать optional My Work task без изменения Alarm acknowledgement;
- видеть список и counters «Моя работа»;
- принять, передать или вернуть задачу с причиной согласно существующей state machine;
- видеть realtime/refresh обновление assignment/status/due.

## 5. Объём реализации

- Production DI и configuration для Incident/MyWork stores.
- Server endpoints и transport DTO для стабильных операций.
- Идемпотентная связь event → incident и optional task.
- Web routes/components для compact incident view и My Work list/detail/actions.
- Deep links обратно к source event/equipment.
- Browser/integration tests:
  - create incident;
  - duplicate request/idempotency;
  - create optional task;
  - accept/transfer/return;
  - permission-filtered candidates;
  - source alarm facets не изменяются.

## 6. Архитектурные требования

- Incident, Alarm occurrence и My Work task — разные identities.
- Создание Incident не выполняет acknowledgement.
- Task transition не выполняет Incident/Alarm transition неявно.
- Полный incident crisis workspace не проектируется.
- Viewer не получает недоступные source labels через link.

## 7. Критерии приёмки

- Core сценарии доступны через Server и Web.
- Actions проверяют session, permission, version и audit.
- Duplicate command не создаёт второй Incident/task.
- Existing Incident/MyWork/Event tests проходят.
- Browser tests подтверждают независимость сущностей.

## 8. За пределами задания

- Comments/participants/SLA/full incident lifecycle.
- Crisis mode.
- Reports и shift journal.

## 9. Итоговый отчёт

Указать API surface, реализованные transitions и доказательство независимости Alarm/Incident/Task.

