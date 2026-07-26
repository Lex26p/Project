# C19 — Administration, operational health и profile completion

## 1. Назначение

Завершить стабильные административные и персональные Web-сценарии поверх существующих Identity/Administration/Workspace модулей.

## 2. Архитектурный контекст

Прочитать:

- `WEB_INTERFACE_SPECIFICATION.md`, разделы 25, 27–30;
- `WEB_BACKEND_API_REQUIREMENTS.md`, разделы 5.1, 5.2 и 5.12;
- `docs/ADR-010_LOCAL_PRODUCTION_AUTHENTICATION.md`;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- `src/Dispatcher.Server/IdentityEndpoints.cs`
- `src/Dispatcher.Server/AdministrationEndpoints.cs`
- `src/Dispatcher.Identity/IdentityModels.cs`
- `src/Dispatcher.Identity/IdentityStore.cs`
- `src/Dispatcher.Administration/AdministrationModels.cs`
- `src/Dispatcher.Administration/AdministrationStore.cs`
- `src/Dispatcher.Workspace/*`
- `src/Dispatcher.Web/Pages/IdentityAdministration.razor`
- `src/Dispatcher.Web/Pages/Operations.razor`
- `src/Dispatcher.Web/Pages/Me.razor`
- `src/Dispatcher.Web/Pages/UserProfile.razor`
- `src/Dispatcher.Web/Pages/Search.razor`
- `src/Dispatcher.Web/IdentityApiClient.cs`
- `src/Dispatcher.Web/OperationsApiClient.cs`
- `tests/Dispatcher.IntegrationTests/IdentityProductionAuthenticationTests.cs`
- `tests/Dispatcher.IntegrationTests/AdministrationOperationsTests.cs`
- `tests/Dispatcher.IntegrationTests/WorkspaceTests.cs`

Остальные файлы изучать по необходимости.

## 4. Целевой результат

- Session bootstrap/header counters/home context завершены.
- «Моя страница» и viewer-filtered person page различаются.
- Profile settings изменяют только разрешённые локальные поля.
- Search/favorites/recent сохраняют permission boundary.
- Admin может просматривать и управлять существующими accounts/roles/groups/scopes через preview + apply.
- Operations показывает platform/runtime/source/downstream health, data quality и audit как разные facets.
- Session revoke/permission change сразу отражаются в Web/realtime.

## 5. Объём реализации

- Добавить недостающие read endpoints к существующим Identity commands.
- Добавить effective permission/source/impact read models.
- Завершить Web pages и shell integration.
- Подключить runtime readiness C03/C05 к operational health.
- Audit остаётся immutable/read-only.
- Browser E2E:
  - own vs other profile;
  - hidden fields;
  - role impact/apply;
  - last-admin protection;
  - session revoke;
  - health facet degradation;
  - audit visibility;
  - search inaccessible target.

## 6. Архитектурные требования

- Account и person не объединяются.
- Должность не предоставляет permission.
- Admin UI не заменяет backend authorization.
- Health платформы, data quality и технологический Alarm не объединяются.
- Secrets/credential evidence отсутствуют в diagnostics/audit.
- Не проектировать внешний IAM в этой задаче.

## 7. Критерии приёмки

- Viewer filtering подтверждён tests.
- Role/permission change инвалидирует затронутую session.
- Health показывает RuntimeHost heartbeat/delivery lag без internal payload.
- Audit read-only.
- Existing identity/administration/workspace и browser tests проходят.

## 8. За пределами задания

- Внешний IAM/SSO.
- HR master data integration.
- Социальная лента.
- Provisional integration catalog CRUD.

## 9. Итоговый отчёт

Указать read/command surface, invalidation behavior и operational health facets.

