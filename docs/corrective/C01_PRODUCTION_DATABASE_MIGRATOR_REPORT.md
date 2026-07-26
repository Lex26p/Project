# C01 — Production DatabaseMigrator: итоговый отчёт и запуск

## 1. Результат

В solution добавлен one-shot executable `Dispatcher.DatabaseMigrator`. Он является единственным production-механизмом применения существующих PostgreSQL migrations перед запуском `Dispatcher.RuntimeHost` и `Dispatcher.Server`.

Порядок запуска production-процессов:

1. PostgreSQL 17 и внешнее provisioning ролей.
2. `Dispatcher.DatabaseMigrator`.
3. `Dispatcher.RuntimeHost`.
4. `Dispatcher.Server` и Web.

`RuntimeHost` и `Server` не применяют migrations автоматически.

C01 завершён после успешной финальной Release-сборки и полного набора тестов repository.

## 2. Конфигурация

Конфигурация передаётся через переменные окружения текущего процесса или Windows service/deployment environment. Значения secret не хранятся в repository и не передаются аргументами командной строки.

Обязательные имена настроек:

- `DISPATCHER_MIGRATIONS_CONNECTION_STRING`
- `DISPATCHER_MIGRATIONS_ROLE__<owner>`

Примеры имён role mappings без значений:

- `DISPATCHER_MIGRATIONS_ROLE__administration`
- `DISPATCHER_MIGRATIONS_ROLE__alarm_runtime`
- `DISPATCHER_MIGRATIONS_ROLE__personal_workspace`

Для каждого owner требуется отдельное mapping. Неизвестный owner, отсутствующее mapping или некорректный PostgreSQL identifier блокируют запуск до изменения базы.

## 3. Граница PostgreSQL-администрирования

`Dispatcher.DatabaseMigrator` не создаёт PostgreSQL users или roles. Их создаёт администратор до запуска executable.

Migration principal:

- не должен требовать superuser в production;
- должен подключаться к целевой базе;
- должен иметь право `SET ROLE` для каждой из 19 настроенных owner roles;
- не должен использоваться RuntimeHost или Server как обычная runtime identity.

Connection string, пароль и полная конфигурация не выводятся в консоль.

## 4. Команды executable

Показать справку:

```powershell
Dispatcher.DatabaseMigrator.exe --help
```

Проверить и вывести стабильный каталог без подключения к PostgreSQL:

```powershell
Dispatcher.DatabaseMigrator.exe --list-plans
```

Проверить переменные окружения без подключения к PostgreSQL:

```powershell
Dispatcher.DatabaseMigrator.exe --validate-config
```

Проверить PostgreSQL 17+, наличие ролей и разрешения `SET ROLE` без применения migrations:

```powershell
Dispatcher.DatabaseMigrator.exe --preflight
```

Применить все отсутствующие migrations:

```powershell
Dispatcher.DatabaseMigrator.exe
```

При запуске из исходного проекта используется эквивалентная команда:

```powershell
dotnet run --project ".\src\Dispatcher.DatabaseMigrator\Dispatcher.DatabaseMigrator.csproj" -c Release --no-build
```

## 5. Exit codes

| Код | Значение |
|---:|---|
| `0` | Команда успешно завершена |
| `2` | Некорректный вызов или конфигурация |
| `3` | Ошибка PostgreSQL preflight |
| `4` | Ошибка применения migration plan |
| `130` | Выполнение отменено через cancellation/Ctrl+C |

Любая ошибка завершает executable ненулевым кодом. После ошибки owner следующие plans не запускаются.

## 6. Фактический порядок owner plans

Каталог задан явно в коде и не зависит от reflection, файловой системы или порядка загрузки assemblies:

1. `administration`
2. `alarm_runtime`
3. `command`
4. `configuration_release`
5. `core_runtime`
6. `dashboards`
7. `equipment_registry`
8. `event_journal`
9. `facility_model`
10. `history`
11. `identity`
12. `incidents`
13. `maintenance`
14. `my_work`
15. `notifications`
16. `platform_nucleus`
17. `simulator_runtime`
18. `terminals`
19. `personal_workspace`

Каталог проверяет ровно 19 registrations, уникальность owner/schema и соответствие каждого registration возвращаемому migration plan.

## 7. Поведение выполнения

Перед первым изменением базы executable:

1. Читает и полностью проверяет конфигурацию.
2. Проверяет PostgreSQL major version 17 или новее.
3. Проверяет существование всех уникальных ролей.
4. В откатываемых транзакциях проверяет реальную возможность `SET LOCAL ROLE`.
5. Только после успешного общего preflight запускает plans в фиксированном порядке.

Каждый owner выполняется существующим `PostgresMigrationRunner` с advisory lock, собственной транзакцией, журналом `__dispatcher_migrations` и checksum validation.

Повторный запуск идемпотентен: уже применённые версии с совпадающим checksum не выполняются повторно. Несовпадение сохранённого checksum является ошибкой и блокирует продолжение.

## 8. Подтверждённые сценарии

Реализованы автоматические проверки:

- fresh apply всех 19 plans;
- создание всех 19 schemas и migration histories;
- repeat apply с `0` новыми steps;
- отсутствующее owner-role mapping до изменения базы;
- checksum conflict;
- остановка последовательности после ошибки owner;
- cancellation до чтения конфигурации;
- справка без обязательной конфигурации.

Во время ручной локальной проверки PostgreSQL 17 первый запуск успешно обработал 19 owners и применил 32 migration steps с exit code `0`.

## 9. Финальная проверка C01

Финальная приёмка выполнена 26 июля 2026 года из корня repository:

```powershell
dotnet restore Dispatcher.slnx
```

```powershell
dotnet build Dispatcher.slnx -c Release --no-restore
```

```powershell
dotnet test Dispatcher.slnx -c Release --no-build
```

Результат финальной приёмки:

- restore завершён успешно;
- Release-сборка всех проектов завершена успешно;
- `Dispatcher.DatabaseMigrator` собран успешно;
- полный test suite: 172 теста, 172 успешно, 0 сбоев, 0 пропущено;
- информационное сообщение `NETSDK1057` о предварительной версии .NET не является предупреждением компилятора или анализатора.

C01 переведён в `Complete`. Непосредственная зависимость C02 переведена в `Ready`.
