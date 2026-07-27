# C06 — Same-origin Web, production session и runtime realtime: итоговый отчёт

## 1. Результат

Server теперь размещает Blazor WebAssembly assets, API и SignalR hubs в одном origin. API и hubs регистрируются до client-route fallback; прямой переход и reload Web-route возвращают `index.html`. Для HTML установлен `Cache-Control: no-store, no-cache`, при этом fingerprinted Web assets сохраняют штатную cache policy.

Production runtime-сессия использует один client state для login, refresh, revoke и server-side invalidation. Статическая настройка runtime scope удалена: Web получает разрешённые scopes и default scope через авторизованный session bootstrap.

## 2. Session bootstrap и scope

Добавлен endpoint:

`GET /api/auth/bootstrap`

Он принимает только актуальную production session и возвращает:

- account, session и subject identifiers;
- срок действия session;
- permission-filtered список scopes;
- default scope;
- effective permissions.

Scope формируется на сервере из scoped role grants текущего account и дополнительно фильтруется актуальными effective permissions. Primary scope становится default только если он входит в разрешённый набор; иначе выбирается первый разрешённый scope. `Current` больше не читает scope из `wwwroot/appsettings.json`.

## 3. Client session lifecycle

`IdentitySessionState` хранит production session, bootstrap и монотонную generation.

- Login и refresh сначала получают credential, затем тем же credential читают bootstrap.
- Только успешно проверенная пара session/bootstrap становится текущим состоянием.
- Общий same-origin `HttpClient` получает `Authorization: Dispatcher-Session <token>` из единого session state.
- Logout, неуспешный login/bootstrap, revoke и realtime permission invalidation очищают session, bootstrap и HTTP authorization header.
- Смена session увеличивает generation и инвалидирует ранее показанные runtime points и защищённое UI state.

## 4. Runtime realtime transport

Runtime `HubConnection` больше не создаётся при startup Web-приложения.

- Connection создаётся для текущей production session непосредственно перед bootstrap.
- Явно выбран только `HttpTransportType.LongPolling`.
- Credential передаётся custom header `Authorization: Dispatcher-Session <token>`.
- Access token не передаётся через query string или URL.
- При refresh/replacement generation меняется: старое connection уничтожается, новое получает новый header и выполняет новый bootstrap.
- Disconnect требует resnapshot; permission invalidation очищает runtime points и client session.

Command realtime также ограничен Long Polling, чтобы custom production header не терялся при transport fallback.

## 5. Same-origin hosting и security boundary

- `Dispatcher.Server` подключает hosted Blazor WebAssembly project и server package.
- `UseBlazorFrameworkFiles` и static files включены до endpoint dispatch.
- API и hubs регистрируются до `MapFallbackToFile("index.html")`.
- Permissive CORS не добавлялся.
- Production session middleware остаётся обязательным: same-origin сам по себе не предоставляет authorization.
- `TestSessionBridge` не участвует в production session path и в интеграционном тесте явно выключен в `Production` environment.

## 6. Проверенные сценарии

- `/` и прямой `/current` возвращают Web index;
- HTML route имеет no-store cache policy;
- anonymous runtime snapshot получает `401`;
- production login и bootstrap возвращают permission-filtered default scope;
- SignalR negotiate/poll используют точный `Dispatcher-Session` header и Long Polling;
- наблюдавшиеся hub URL не содержат access или refresh token;
- refresh инвалидирует старый access token, новый token получает bootstrap;
- изменение role permissions инвалидирует активную session;
- revoke инвалидирует replacement session;
- session replacement очищает ранее авторизованные runtime values;
- существующие gap, reconnect и point-permission invalidation realtime-сценарии сохранены.

## 7. Изменённые файлы

### Identity

- `src/Dispatcher.Identity/IdentityModels.cs`
- `src/Dispatcher.Identity/IdentityStore.cs`

### Server hosting и API

- `Directory.Packages.props`
- `src/Dispatcher.Server/Dispatcher.Server.csproj`
- `src/Dispatcher.Server/IdentityEndpoints.cs`
- `src/Dispatcher.Server/Program.cs`
- `src/Dispatcher.Server/packages.lock.json`

### Web

- `src/Dispatcher.Web/Program.cs`
- `src/Dispatcher.Web/IdentityApiClient.cs`
- `src/Dispatcher.Web/RealtimeWidgetClient.cs`
- `src/Dispatcher.Web/RealtimeWidgetState.cs`
- `src/Dispatcher.Web/CommandRealtimeClient.cs`
- `src/Dispatcher.Web/Pages/Current.razor`
- `src/Dispatcher.Web/wwwroot/appsettings.json`

### Tests и corrective documentation

- `tests/Dispatcher.UnitTests/RealtimeWidgetStateTests.cs`
- `tests/Dispatcher.IntegrationTests/ServerProductionSessionTests.cs`
- `tests/Dispatcher.IntegrationTests/packages.lock.json`
- `docs/corrective/STATUS.md`
- `docs/corrective/C06_SAME_ORIGIN_WEB_SESSION_REALTIME_REPORT.md`

## 8. Проверки

Выполнены обязательные команды:

```powershell
dotnet restore Dispatcher.slnx --locked-mode
dotnet build Dispatcher.slnx --configuration Release --no-restore
dotnet test tests/Dispatcher.UnitTests/Dispatcher.UnitTests.csproj --configuration Release --no-build --no-restore
dotnet test tests/Dispatcher.IntegrationTests/Dispatcher.IntegrationTests.csproj --configuration Release --no-build --no-restore
```

Результат:

- locked restore успешен;
- Release build: 0 предупреждений, 0 ошибок;
- Unit: 109 успешно, 0 сбоев, 0 пропущено;
- Integration: 119 успешно, 0 сбоев, 0 пропущено;
- всего: 228 тестов успешно.

Дополнительно отдельно прошли новые production-session/fallback integration test, 3 Server realtime tests и 3 `RealtimeWidgetState` tests.

Использовались временные PostgreSQL databases. Docker, Linux и Git не использовались. Blocking decisions и оставшиеся ограничения в объёме C06 отсутствуют.
