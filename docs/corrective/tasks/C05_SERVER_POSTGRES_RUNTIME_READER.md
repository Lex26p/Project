# C05 — Server читает runtime current из PostgreSQL

## 1. Назначение

Удалить производственную зависимость Server от локального `RuntimeRegistry` и подключить HTTP/SignalR к published current contract C03.

## 2. Архитектурный контекст

Прочитать:

- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 3.3, 4 и 10;
- `docs/ADR-005_SESSION_SECURITY_NUCLEUS.md`;
- `docs/ADR-006_WEB_REALTIME_TRANSPORT.md`;
- `docs/ADR-007_BOUNDED_RUNTIME_CURRENT.md`;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- Core published reader/migrations, созданные C03
- `src/Dispatcher.Server/Program.cs`
- `src/Dispatcher.Server/ServerComposition.cs`
- `src/Dispatcher.Server/RuntimeAccess.cs`
- `src/Dispatcher.Server/RuntimeContracts.cs`
- `src/Dispatcher.Server/RuntimeRealtimeHub.cs`
- `src/Dispatcher.Server/TestSessionBridge.cs`
- `tests/Dispatcher.IntegrationTests/ServerRealtimeTests.cs`
- `tests/Dispatcher.IntegrationTests/CoreRuntimeRecoveryTests.cs`

Остальные файлы изучать по необходимости.

## 4. Целевой результат

Server получает current snapshot, deltas и runtime readiness асинхронно из PostgreSQL под отдельной read-only role. Отдельно запущенный RuntimeHost становится видим Server без общей памяти и ручной регистрации.

Сохраняется permission filtering и разделение private core cursor / Web cursor.

## 5. Объём реализации

- Production registration PostgreSQL published reader.
- Замена синхронного `AuthorizedRuntimeReader` на async contract.
- Адаптация snapshot endpoint и `RuntimeRealtimeHub`.
- Удаление `RuntimeRegistry` из production composition; если он нужен unit tests, явно локализовать test-only use.
- Настройки connection/current read role и bounded query limits.
- Mapping published point/readiness в существующие transport DTO без утечки internal delivery data.
- Readiness endpoint/read model для scope.
- Integration tests с RuntimeHost-writer и Server-reader roles.

## 6. Архитектурные требования

- Server не получает Core writer role.
- Server не читает recovery checkpoint/source obligation.
- Permission filtering выполняется после server-side session authorization.
- Hidden changes продвигают private cursor, но не Web cursor.
- Gap очищает subscription и требует bootstrap.
- Revoked/expired/replaced session инвалидирует subscription.
- Query limits не зависят от пользовательского payload без server cap.

## 7. Критерии приёмки

- Snapshot видит данные, опубликованные отдельной runtime composition.
- Delta/no-change/gap работают через PostgreSQL.
- Point-level permission test не раскрывает hidden point или факт его изменения.
- Read role не может записать current и прочитать internal tables.
- Server startup без current configuration fail-closed для runtime endpoints, но не включает in-memory fallback.
- Existing Server realtime tests сохраняют семантику.

## 8. За пределами задания

- Web session wiring и static hosting.
- UI.
- History/Event API изменения.
- Runtime pipeline.

## 9. Итоговый отчёт

Указать production reader, PostgreSQL privileges и результаты permission/cursor tests.

