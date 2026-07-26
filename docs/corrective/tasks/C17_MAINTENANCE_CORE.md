# C17 — Maintenance Core API/Web

## 1. Назначение

Подключить существующий Maintenance/ППР контур к Server и Web без выдумывания provisional lifecycle.

## 2. Архитектурный контекст

Прочитать:

- `WEB_INTERFACE_SPECIFICATION.md`, разделы 23 и 24;
- `WEB_BACKEND_API_REQUIREMENTS.md`, раздел 5.11;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- `src/Dispatcher.Maintenance/MaintenanceModels.cs`
- `src/Dispatcher.Maintenance/MaintenancePlanCalendar.cs`
- `src/Dispatcher.Maintenance/MaintenanceScheduler.cs`
- `src/Dispatcher.Maintenance/MaintenanceService.cs`
- `src/Dispatcher.Maintenance/MaintenanceStore.cs`
- `src/Dispatcher.Maintenance/MaintenanceWorkModels.cs`
- `src/Dispatcher.Maintenance/MaintenanceWorkService.cs`
- `src/Dispatcher.Maintenance/MaintenanceWorkStore.cs`
- `src/Dispatcher.Equipment/EquipmentService.cs`
- Incident/MyWork integration C16
- `tests/Dispatcher.IntegrationTests/MaintenanceAssetTests.cs`
- `tests/Dispatcher.IntegrationTests/MaintenanceSchedulerTests.cs`
- `tests/Dispatcher.IntegrationTests/MaintenanceWorkTests.cs`

Остальные файлы изучать по необходимости.

## 4. Целевой результат

Web/Server поддерживают проверенный Core-контур:

- Maintenance overview;
- service assets и связь/standalone/review state;
- plan/forecast/calendar read models;
- maintenance requests, defects и work orders;
- создание request из Event;
- `Overdue → Assigned → Accepted → InProgress → PendingAcceptance → Completed`;
- обязательный checklist;
- синхронизацию assignment с My Work.

## 5. Объём реализации

- Production DI/configuration и endpoints с pagination/filter/version.
- Permission-filtered read models и allowed actions.
- Web overview, assets, calendar/forecast, work list и work-order detail.
- Checklist/safety state в существующей модели.
- Deep links Event/Equipment/MyWork.
- Realtime либо bounded refresh для status/assignment/checklist.
- Browser/integration tests всего зафиксированного lifecycle.

## 6. Архитектурные требования

- Maintenance asset может быть standalone без telemetry equipment.
- Forecast, work order и My Work task остаются разными сущностями.
- Обязательный checklist блокирует передачу на приёмку.
- Завершение work order не подтверждает исходный Alarm.
- Не добавлять pause/resume, contractor portal, ресурсы или полный CRUD plan без отдельного продукта.

## 7. Критерии приёмки

- Lifecycle проходит только допустимые transitions.
- Concurrency conflict возвращает актуальное состояние.
- My Work assignment согласован без слияния identities.
- Browser deep links и permission filtering работают.
- Existing Maintenance tests проходят.

## 8. За пределами задания

- Полный ERP/EAM.
- Mobile technician offline workplace.
- Contractors/resources/inventory.

## 9. Итоговый отчёт

Указать реализованные read models/transitions и явно перечислить provisional возможности, которые не добавлялись.

