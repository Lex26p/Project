# C16 — Incident и My Work Core API/Web: итоговый отчёт

## 1. Результат

Incident и My Work подключены к production Server/Web без объединения их identities с Alarm occurrence.

Реализованы:

- создание Incident из точного Event либо актуального Event для occurrence;
- идемпотентное создание связи с источником и optional My Work task;
- compact incident summary с permission-filtered source links;
- очередь `/my-work`, четыре вычисляемых counters и task detail;
- `Accept`, `Transfer` и `Return` с optimistic version и idempotency key;
- обязательная причина для transfer/return;
- permission-filtered поиск кандидатов;
- bounded refresh assignment/status/due;
- deep links Incident → Event и My Work → Incident.

## 2. API surface

- `POST /api/events/{scopeId}/{eventId}/incident`
- `POST /api/events/{scopeId}/occurrences/{occurrenceId}/incident`
- `GET /api/incidents/{incidentId}`
- `GET /api/incidents/{incidentId}/sources/{linkId}`
- `GET /api/my-work/`
- `GET /api/my-work/counters`
- `GET /api/my-work/tasks/{taskId}`
- `GET /api/my-work/transfer-candidates?query=...`
- `POST /api/my-work/tasks/{taskId}/accept`
- `POST /api/my-work/tasks/{taskId}/transfer`
- `POST /api/my-work/tasks/{taskId}/return`

Production composition использует отдельные роли:

- `Dispatcher:Incidents:DatabaseRole`
- `Dispatcher:MyWork:DatabaseRole`

## 3. Persistence и transitions

Добавлены version 2 migrations:

- Incident task: `due_at`, `last_transition_reason`;
- Incident mutation audit: `reason`;
- My Work projection: `due_at`, `last_transition_reason`;
- bounded indexes для due queries.

Зафиксированные transitions:

- `Offered → Accepted`;
- `Offered/Accepted/Returned → Offered` при передаче другому разрешённому исполнителю;
- `Offered/Accepted → Returned` с возвратом координатору.

Только текущий исполнитель может выполнить transition. Transfer и Return требуют причину. Каждое изменение проверяет session, permission, expected version и idempotency key и сохраняется в Incident audit.

## 4. Независимость сущностей

- Incident получает собственный `IncidentId`.
- Source Alarm occurrence сохраняет собственный `AlarmOccurrenceId`.
- My Work task получает собственный `IncidentTaskId`.
- Создание Incident/task и task transitions не вызывают Alarm acknowledgement.
- My Work является rebuildable projection и не становится owner состояния Incident или Alarm.
- Source link выдаётся только при наличии всех требуемых permissions.

## 5. Web

- В Event Dispatcher добавлено действие создания Incident и optional task.
- Добавлен compact маршрут `/incidents/{id}`.
- Добавлена очередь `/my-work` с counters, detail и действиями.
- My Work доступен из header/avatar area, а не из основной навигации.
- Assignment/status/due обновляются bounded polling раз в две секунды.
- Candidate query и reason обновляются на `oninput`, чтобы исключить отправку устаревшего значения.

## 6. Проверки

- `Dispatcher.Incidents` Release build — успешно, 0 warnings, 0 errors.
- `Dispatcher.MyWork` Release build — успешно, 0 warnings, 0 errors.
- `Dispatcher.Web` Release build — успешно, 0 warnings, 0 errors.
- `Dispatcher.Server` Release build — успешно, 0 warnings, 0 errors.
- `Dispatcher.IntegrationTests` Release compilation — успешно, 0 warnings, 0 errors.
- `Dispatcher.BrowserTests` Release compilation — успешно, 0 warnings, 0 errors.
- `IncidentMyWorkTests` — 1/1 успешно.
- `EventDispatcherTests` и `RuntimeEventDeliveryProcessorTests` — 7/7 успешно.
- `C16IncidentMyWorkBrowserTests` — 1/1 успешно.

Integration test подтверждает:

- create/replay Incident;
- create/replay optional task;
- source permission denial;
- My Work permission filtering;
- due counters;
- accept/transfer/return;
- сохранение transition reason в audit;
- Alarm остаётся `Unacknowledged`.

Browser test подтверждает:

- создание Incident из Event Dispatcher;
- compact summary и разрешённый source link;
- появление task в My Work;
- accept и permission-filtered transfer candidate;
- исчезновение задачи после передачи;
- Alarm остаётся `Unacknowledged`.

Общий browser fixture получил bounded publish timeout 180 секунд, поскольку Release publish с trimming на текущей Windows-машине стабильно превышает прежние 60 секунд.

## 7. Ограничения

- Полный incident/crisis workspace, comments, participants, SLA и произвольный lifecycle не добавлялись.
- Для realtime используется разрешённый заданием bounded refresh; отдельный Incident stream не вводился.
- Physical Modbus/SNMP команды и устройства не использовались.
