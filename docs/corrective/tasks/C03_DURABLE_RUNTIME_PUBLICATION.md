# C03 — Durable processing delivery и published current

## 1. Назначение

Создать Core-owned durable границу между применением runtime fact, downstream processing и публикацией current для Server.

## 2. Архитектурный контекст

Прочитать:

- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 4–6, 10 и 16;
- `docs/ADR-004_POSTGRESQL_PERSISTENCE.md`;
- `docs/ADR-007_BOUNDED_RUNTIME_CURRENT.md`;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- `src/Dispatcher.Core/CoreRuntime.cs`
- `src/Dispatcher.Core/CoreRuntimeHost.cs`
- `src/Dispatcher.Core/CoreRuntimeStore.cs`
- `src/Dispatcher.Core/CoreRuntimeMigrations.cs`
- `src/Dispatcher.Core/CurrentModels.cs`
- `src/Dispatcher.Core/RuntimeRecoveryModels.cs`
- `src/Dispatcher.Core/SourceBinding.cs`
- `src/Dispatcher.RuntimeHost/RuntimeProcess.cs`
- DatabaseMigrator/catalog, созданный C01
- runtime worker, созданный C02
- `tests/Dispatcher.IntegrationTests/CoreRuntimeRecoveryTests.cs`
- `tests/Dispatcher.UnitTests/RealtimeWidgetStateTests.cs`

Остальные файлы изучать по необходимости.

## 4. Целевой результат

Core runtime поддерживает следующий durable lifecycle:

`protected obligation → apply/checkpoint + pending delivery → downstream acknowledgement → publish current/delta + delivery complete`.

После crash между любыми двумя стадиями RuntimeHost восстанавливает точную pending delivery и не применяет следующий fact до её завершения.

Server-facing published state хранится отдельно от recovery internals и доступен через read-only contract.

## 5. Объём реализации

- Новая Core migration version для:
  - source session generation, если не завершено C02;
  - processing delivery;
  - published scope/readiness;
  - published current;
  - retained published deltas.
- Rich processing outcome вместо результата, содержащего только `bool`, либо эквивалентный testable contract.
- Atomic checkpoint + pending delivery.
- Atomic current/delta publication + delivery completion.
- Сериализация/хранение достаточного post-cut acceptance для replay Alarm processing.
- Восстановление незавершённой delivery до открытия admission.
- Ограниченная delta retention и cursor-gap semantics.
- Bounded cleanup завершённых obligations после safety window.
- Core read methods для consistent snapshot/delta/readiness, пригодные для C05.
- Read-only published role в Core migration contract и соответствующее расширение настроек DatabaseMigrator.

Физическая схема может отличаться от названий спецификации, но права и транзакционные границы сохраняются.

## 6. Архитектурные требования

- Не публиковать current до явного завершения delivery.
- Не обрабатывать две delivery одного scope параллельно.
- Не удалять obligation до durable checkpoint и delivery completion.
- Replay с тем же содержанием идемпотентен; другое содержимое на той же identity конфликтует.
- Snapshot и cursor читаются согласованно.
- Delta pruning не изменяет snapshot.
- Server-read role не видит recovery payload и pending delivery.
- Не хранить бесконечный journal.

## 7. Обязательные fault tests

- Crash после append obligation, до apply.
- Crash после checkpoint/pending delivery, до downstream completion.
- Crash после downstream completion, до publication.
- Crash после publication commit.
- Повтор finalize той же delivery.
- Попытка finalize не по порядку.
- Cursor старше retention и cursor впереди current.
- Cleanup не удаляет незавершённую или recovery-required запись.

## 8. Критерии приёмки

- После каждого fault restart приводит к одному опубликованному результату.
- Следующий fact не обгоняет pending delivery.
- Current snapshot сохраняет bounded point capacity.
- Delta storage остаётся в заданной capacity.
- Права read-role подтверждены integration test.
- Existing Core tests адаптированы без ослабления их семантики.

## 9. За пределами задания

- Реальная History/Alarm/Event delivery.
- Server endpoints.
- SignalR/Web.
- Protocol activation.

## 10. Итоговый отчёт

Описать фактические transaction boundaries, recovery algorithm и retention rule.
