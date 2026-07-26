# C04 — Автоматическая History → Alarm → Event pipeline

## 1. Назначение

Подключить существующие History, Alarm и Event stores к durable processing delivery RuntimeHost.

## 2. Архитектурный контекст

Прочитать:

- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 6 и 9;
- `docs/ADR-003_SEMANTIC_CONTRACTS.md`;
- `docs/ADR-004_POSTGRESQL_PERSISTENCE.md`;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- delivery/publication contract, созданный C03
- runtime worker и `RuntimeProcess`
- `src/Dispatcher.History/HistoryStore.cs`
- `src/Dispatcher.History/HistoryModels.cs`
- `src/Dispatcher.Alarm/AlarmEvaluator.cs`
- `src/Dispatcher.Alarm/AlarmStore.cs`
- `src/Dispatcher.Alarm/AlarmModels.cs`
- `src/Dispatcher.Events/EventStore.cs`
- `src/Dispatcher.Events/EventModels.cs`
- `tests/Dispatcher.IntegrationTests/HistoryAcceptanceTests.cs`
- `tests/Dispatcher.IntegrationTests/AlarmEvaluationTests.cs`
- `tests/Dispatcher.IntegrationTests/EventDispatcherTests.cs`
- `tests/Dispatcher.IntegrationTests/ProtocolCommissioningAcceptanceTests.cs`

Остальные файлы изучать по необходимости.

## 4. Целевой результат

RuntimeHost содержит testable delivery coordinator, который для одной pending delivery:

1. идемпотентно передаёт obligation в History;
2. для SourceCut выполняет Alarm evaluation на точном post-cut snapshot;
3. передаёт все изменившиеся occurrence versions в EventStore;
4. завершает и публикует Core delivery только после успеха всех обязательных стадий.

При повторе уже завершённые стадии не создают вторых samples, occurrences или events.

## 5. Объём реализации

- Добавить RuntimeHost references и configuration для History/Alarm/Event roles и limits.
- Создать coordinator с bounded retry/backoff и cancellation.
- Использовать существующие store idempotency contracts, не писать напрямую в чужие schemas.
- Не использовать `RuntimeObligationCommitHook` как единственный источник delivery; replay запускается из persisted pending delivery C03.
- Связать Alarm definition epoch с active runtime configuration revision.
- Поддержать явно активный пустой definition set.
- Для gap выполнить History acceptance и обновить continuity без фиктивной Alarm evaluation.
- Сохранять безопасный stage/error code в pending delivery.
- Добавить pipeline integration tests с реальным PostgreSQL.

## 6. Архитектурные требования

- Порядок History → Alarm → Event фиксирован.
- Следующий Core fact не обрабатывается до завершения текущей delivery.
- Failure downstream не публикует новый current.
- Нельзя заменять idempotency распределённой transaction иллюзией.
- Alarm action пользователя не входит в runtime coordinator; существующий Server path сохраняется.
- Notification, Incident и Maintenance не добавляются как синхронные runtime blockers.

## 7. Обязательные fault tests

- History commit succeeded, callback/process crashed.
- Alarm failure после accepted History.
- Event failure после persisted occurrence.
- Restart на каждой стадии.
- Duplicate delivery.
- Empty alarm definition set.
- Gap delivery.

## 8. Критерии приёмки

- Один Simulator cut автоматически создаёт History sample.
- Пересечение threshold создаёт Alarm occurrence и Event.
- Return-to-normal создаёт следующую согласованную version/event.
- Replay не умножает данные.
- До pipeline success published cursor не изменяется.
- Pipeline recovery завершает delivery после restart.

## 9. За пределами задания

- Notifications/Incidents/Maintenance side effects.
- UI.
- Configuration editing.
- Modbus/SNMP.

## 10. Итоговый отчёт

Указать стадии coordinator, replay evidence и поведение при каждом injected failure.
