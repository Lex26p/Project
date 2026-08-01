# C17 — Maintenance Core API/Web: итоговый отчёт

## 1. Результат

Maintenance Core подключён к production Server/Web в границах существующей модели.

Реализованы:

- permission-filtered overview по facility scope;
- service assets со standalone, linked и review-required состояниями;
- plan, bounded forecast и calendar read models;
- maintenance requests, defects и work orders;
- создание maintenance request из Event;
- lifecycle `Overdue → Assigned → Accepted → InProgress → PendingAcceptance → Completed`;
- safety acknowledgement и обязательный checklist;
- versioned синхронизация assignment/status с My Work;
- защищённая навигация Maintenance и bounded Web refresh.

## 2. API и read models

В production API доступны:

- `GET /api/maintenance/overview`;
- assets, equipment link/review и link history;
- approved plan detail, forecast и calendar;
- request/defect list, detail и commands;
- work-order list/detail и lifecycle commands;
- Event → maintenance request.

List endpoints используют bounded pagination/filtering. Mutation endpoints проверяют permission, expected version и idempotency key. Concurrency conflict work order возвращает актуальный snapshot вместе с разрешёнными действиями.

Overview содержит только подтверждённые текущей моделью показатели: overdue, due today, requires assignment, in progress, pending acceptance и safety attention.

## 3. Persistence и identities

- Maintenance asset может существовать без telemetry equipment.
- Forecast obligation, work order и My Work projection сохраняют разные identities.
- Request, Defect, Forecast и Work Order остаются разными owner-сущностями.
- My Work является versioned projection назначения, а не owner lifecycle.
- Завершение work order не изменяет исходный Alarm/Event.
- Scheduler materialization остаётся restart-safe и идемпотентной.

## 4. Web

Добавлены:

- `/maintenance` — overview, assets, calendar/forecast и work lists;
- `/maintenance/work-orders/{id}` — lifecycle, source/asset links, safety, checklist и allowed actions;
- Maintenance в permission-filtered основной навигации.

Overview и work-order status обновляются bounded polling раз в две секунды. Web отображает только действия, разрешённые Server payload. При version conflict загружается актуальное состояние.

Deep links связывают Maintenance с Equipment, Event/источником, Work Order и My Work.

## 5. Проверки

- `Dispatcher.Web` Debug build — успешно, 0 warnings, 0 errors.
- `Dispatcher.Server` Debug build — успешно, 0 warnings, 0 errors.
- `Dispatcher.BrowserTests` Debug build — успешно, 0 warnings, 0 errors.
- Maintenance integration tests — 4/4 успешно.
- `C17MaintenanceBrowserTests` — 1/1 успешно.

Integration tests подтверждают persistence, permission filtering, pagination, scheduler recovery, lifecycle, mandatory checklist, overview counters, concurrency/version semantics и My Work sync.

Browser test подтверждает permission-filtered навигацию, overview, review-required asset, Equipment/Work Order deep links, allowed actions и полный lifecycle через Web.

## 6. Не добавлялось

- pause/resume lifecycle;
- contractor portal;
- resources, materials и inventory;
- полный ERP/EAM;
- полный CRUD maintenance plan;
- mobile offline workplace;
- provisional статусы и команды;
- physical Modbus/SNMP команды.

Wiren Board и ИБП для C17 не требовались и не использовались.
