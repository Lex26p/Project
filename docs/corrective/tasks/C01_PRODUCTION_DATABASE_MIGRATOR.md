# C01 — Production DatabaseMigrator

## 1. Назначение

Создать единственный production-механизм применения всех существующих PostgreSQL migrations перед запуском RuntimeHost и Server.

Сейчас migration runner и module plans существуют, но вызываются только тестами. Обычные процессы не должны применять schema автоматически.

## 2. Архитектурный контекст

Прочитать:

- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 3.1, 4 и 18;
- `docs/ADR-004_POSTGRESQL_PERSISTENCE.md`;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- `Dispatcher.slnx`
- `Directory.Build.props`
- `Directory.Packages.props`
- `src/Dispatcher.Persistence/ModuleMigrationPlan.cs`
- `src/Dispatcher.Persistence/PostgresMigrationRunner.cs`
- `src/Dispatcher.Core/CoreRuntimeMigrations.cs`
- `src/Dispatcher.Platform/PlatformMigrations.cs`
- `tests/Dispatcher.IntegrationTests/PostgresPersistenceTests.cs`
- `tests/Dispatcher.IntegrationTests/PostgreSqlClusterFixture.cs`
- все остальные `src/Dispatcher.*/**/*Migrations.cs` — по мере сборки каталога

Остальные файлы изучать по необходимости.

## 4. Целевой результат

В solution существует one-shot executable `Dispatcher.DatabaseMigrator`, который:

- получает migration connection без хранения secret в проекте;
- получает явное сопоставление каждого migration owner с PostgreSQL role;
- содержит полный стабильный каталог всех существующих module plans;
- проверяет дубликаты owner/schema и отсутствие обязательной роли до изменения БД;
- последовательно вызывает существующий `PostgresMigrationRunner`;
- сообщает безопасный итог по owner без connection string и secret;
- возвращает ненулевой exit code при любой ошибке;
- повторно запускается без изменений;
- корректно реагирует на cancellation.

RuntimeHost и Server не получают автоматический вызов migrations.

## 5. Объём реализации

- Новый console project и включение его в solution.
- Testable composition: parsing/validation настроек отделены от top-level entry point.
- Полный каталог 19 существующих migration plans.
- Фиксированный порядок plans, не зависящий от reflection/file-system enumeration.
- Integration tests на чистую PostgreSQL 17:
  - fresh apply всех plans;
  - repeat apply;
  - отсутствующая owner-role mapping;
  - checksum conflict на representative plan;
  - прекращение последовательности после ошибки.
- Обновление package lock и только относящейся к запуску документации.

Исполнитель самостоятельно выбирает формат configuration. Он должен подходить для Windows service/deployment и позволять передавать разные roles владельцам.

## 6. Архитектурные требования

- Не изменять SQL уже существующих migration versions.
- Не создавать PostgreSQL roles из приложения.
- Не использовать superuser как runtime requirement.
- Не продолжать следующие plans после failure.
- Не выводить исключение с конфигурацией целиком.
- Не добавлять EF Core или вторую migration framework.

## 7. Критерии приёмки

- Все 19 module owners присутствуют в каталоге.
- Fresh migration создаёт все schemas и migration histories.
- Второй запуск применяет 0 steps.
- Ошибка одного plan даёт fail-closed результат.
- Existing integration tests продолжают проходить.
- Release build без предупреждений.

## 8. За пределами задания

- Provisioning PostgreSQL users/roles.
- Backup/restore.
- Windows service registration.
- Новые runtime schemas C02+.

## 9. Итоговый отчёт

Дополнительно указать список owner plans в фактическом порядке и пример имён настроек без значений.

