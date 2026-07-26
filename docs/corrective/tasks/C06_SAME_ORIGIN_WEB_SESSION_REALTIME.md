# C06 — Same-origin Web, production session и runtime realtime

## 1. Назначение

Собрать Server и Blazor Web в один production origin и завершить авторизованную runtime-сессию без статического scope и query token.

## 2. Архитектурный контекст

Прочитать:

- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 11 и 12;
- `docs/ADR-005_SESSION_SECURITY_NUCLEUS.md`;
- `docs/ADR-006_WEB_REALTIME_TRANSPORT.md`;
- `docs/ADR-010_LOCAL_PRODUCTION_AUTHENTICATION.md`;
- `WEB_BACKEND_API_REQUIREMENTS.md`, разделы 4, 5.1 и 7;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- `src/Dispatcher.Server/Program.cs`
- `src/Dispatcher.Server/Dispatcher.Server.csproj`
- `src/Dispatcher.Server/ServerComposition.cs`
- `src/Dispatcher.Server/IdentityEndpoints.cs`
- `src/Dispatcher.Server/RuntimeRealtimeHub.cs`
- `src/Dispatcher.Server/TestSessionBridge.cs`
- `src/Dispatcher.Web/Program.cs`
- `src/Dispatcher.Web/Dispatcher.Web.csproj`
- `src/Dispatcher.Web/IdentityApiClient.cs`
- `src/Dispatcher.Web/RealtimeWidgetClient.cs`
- `src/Dispatcher.Web/RealtimeWidgetState.cs`
- `src/Dispatcher.Web/Pages/Current.razor`
- `src/Dispatcher.Web/wwwroot/index.html`
- `src/Dispatcher.Web/wwwroot/appsettings.json`

Остальные файлы изучать по необходимости.

## 4. Целевой результат

- Server публикует Web assets и client route fallback.
- Login/refresh/logout управляют одной client session state.
- HTTP API и SignalR используют текущую production session.
- Runtime hub подключается через Long Polling с `Dispatcher-Session` header.
- После refresh/replacement connection пересоздаётся и делает bootstrap.
- Runtime scope берётся из permission-filtered session/workspace bootstrap.
- Production не зависит от `TestSessionBridge`.

## 5. Объём реализации

- Настроить hosted Blazor WebAssembly в Server.
- Правильно упорядочить static files, API/hubs и fallback.
- Добавить/расширить session bootstrap contract с разрешёнными scopes и default scope.
- Централизовать применение/очистку session к HTTP clients.
- Сделать lifecycle HubConnection зависимым от актуальной session, а не от startup singleton state.
- Явно выбрать Long Polling; не допускать WebSocket fallback, который потеряет custom header.
- Удалить runtime scope из обязательной статической Web-конфигурации.
- Проверить direct client route, reload и cache policy.
- Добавить integration tests auth header, refresh, revoke, permission change и fallback.

## 6. Архитектурные требования

- Token отсутствует в URL/query/log.
- Anonymous client не получает runtime snapshot.
- Session expiry очищает runtime points и защищённый UI state.
- Same-origin не заменяет authorization.
- Не включать permissive CORS.
- Test bridge остаётся доступен только в явно разрешённых Development/Test сценариях.

## 7. Критерии приёмки

- Один Server process отдаёт `/`, client route, API и hub.
- Авторизованный Web получает current; anonymous получает отказ.
- Refresh/reconnect работает без перезагрузки приложения.
- Revoke/expiry очищает состояние.
- Scope отсутствует в `wwwroot/appsettings.json` как production requirement.
- URL и логи не содержат session token.

## 8. За пределами задания

- Полный визуальный redesign.
- Browser E2E corpus C08+.
- Protocol configuration.

## 9. Итоговый отчёт

Описать session lifecycle, выбранный transport и доказательство отсутствия token в URL.

