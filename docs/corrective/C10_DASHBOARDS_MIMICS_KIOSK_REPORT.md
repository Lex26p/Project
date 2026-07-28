# C10 — Dashboard, mimic и kiosk runtime/editor: итоговый отчёт

## 1. Результат

Реализованы рабочие маршруты каталога Dashboard, runtime, редакторов Dashboard/Mimic и доверенного kiosk.

Dashboard runtime поддерживает:

- каталог опубликованных Dashboard с поиском, избранным и недавними элементами;
- открытие точной опубликованной ревизии и точного окна по deep link;
- bounded current/history/event bindings только для видимого окна;
- widgets, mimic и combined layouts;
- полную resnapshot при публикации новой ревизии;
- безопасный operational fullscreen без физических команд.

Mimic runtime имеет самостоятельную идентичность и использует точную опубликованную ревизию. SVG очищается на Server, а Web применяет к разрешённым binding-элементам только runtime value, quality, freshness и безопасные CSS-классы.

## 2. Authoring и Server

- Dashboard window хранит layout и точную пару `MimicId`/`MimicRevisionId`.
- Runtime manifest разрешает только опубликованные immutable Dashboard/Mimic revisions.
- Dashboard и Mimic проходят draft → save → validate → impact → publish.
- Сохранены optimistic concurrency, серверные validation errors и publication impact.
- Валидация Dashboard проверяет существование и published state каждой указанной Mimic revision.
- Subscription window ограничен bindings выбранного Dashboard window и связанного Mimic.
- Каталог и authoring actions фильтруются эффективными permissions.

## 3. Web и kiosk

- Добавлены страницы каталога, Dashboard runtime, Dashboard editor и Mimic editor.
- Реализованы current value, history trend и event indicator widgets.
- Добавлен безопасный Mimic runtime с масштабированием, fit/reset и управлением слоями.
- Kiosk показывает только назначенный опубликованный read-only контент.
- При потере связи kiosk сохраняет последний разрешённый snapshot, после восстановления выполняет sync/resnapshot.
- Отзыв доверия очищает удержанный контент.
- Authoring, administration и physical commands в kiosk отсутствуют.

## 4. Тесты

Добавлены:

- integration coverage точной опубликованной Mimic revision, sanitization и bounded subscriptions;
- unit coverage очистки удержанного kiosk-контента при revoke;
- пять browser-сценариев C10:
  - каталог, exact deep window, current/history/event и безопасный Mimic;
  - current delta и resnapshot после публикации;
  - Dashboard save/validate/impact/publish;
  - отклонение небезопасного SVG;
  - kiosk assignment, offline retention, reconnect/resync и revoke.

Результаты:

- `dotnet restore Dispatcher.slnx --locked-mode` — успешно;
- `dotnet build Dispatcher.slnx --configuration Release --no-restore --maxcpucount:1 --nodeReuse:false` — успешно, 0 warnings, 0 errors;
- Unit — 139/139 успешно;
- Browser — 19/19 успешно, включая C10 5/5;
- целевые C10 Integration (`DashboardTests`, `TerminalRuntimeTests`, `TerminalEnrollmentTests`) — 8/8 успешно;
- полный Integration — 120/121 в первом прогоне: старый процессный сценарий C07 завершился transient `runtime.lifecycle_state` при остановке RuntimeHost; одиночный повтор того же сценария — 1/1 успешно.

## 5. Изменённые области

### Production

- `src/Dispatcher.Dashboards/DashboardModels.cs`
- `src/Dispatcher.Dashboards/DashboardManifestCodec.cs`
- `src/Dispatcher.Dashboards/DashboardStore.Authoring.cs`
- `src/Dispatcher.Server/DashboardEndpoints.cs`
- `src/Dispatcher.Server/DashboardSubscriptions.cs`
- `src/Dispatcher.Server/DashboardAuthoringService.cs`
- `src/Dispatcher.Server/DashboardAuthoringEndpoints.cs`
- `src/Dispatcher.Server/TerminalRuntimeEndpoints.cs`
- `src/Dispatcher.Server/WorkspaceEndpoints.cs`
- `src/Dispatcher.Web/DashboardApiClient.cs`
- `src/Dispatcher.Web/DashboardContracts.cs`
- `src/Dispatcher.Web/DashboardRuntimeState.cs`
- `src/Dispatcher.Web/DashboardWidgetHost.razor`
- `src/Dispatcher.Web/CurrentValueWidget.razor`
- `src/Dispatcher.Web/EventIndicatorWidget.razor`
- `src/Dispatcher.Web/MimicRuntime.razor`
- `src/Dispatcher.Web/EditorApiClient.cs`
- `src/Dispatcher.Web/EditorContracts.cs`
- `src/Dispatcher.Web/KioskApiClient.cs`
- `src/Dispatcher.Web/KioskContracts.cs`
- `src/Dispatcher.Web/Pages/Dashboards.razor`
- `src/Dispatcher.Web/Pages/DashboardRuntime.razor`
- `src/Dispatcher.Web/Pages/DashboardEditor.razor`
- `src/Dispatcher.Web/Pages/MimicEditor.razor`
- `src/Dispatcher.Web/Pages/Kiosk.razor`
- `src/Dispatcher.Web/wwwroot/app.css`

### Tests

- `tests/Dispatcher.UnitTests/KioskRuntimeStateTests.cs`
- `tests/Dispatcher.IntegrationTests/DashboardTests.cs`
- `tests/Dispatcher.BrowserTests/BrowserScenario.cs`
- `tests/Dispatcher.BrowserTests/C10DashboardMimicKioskBrowserTests.cs`

## 6. Итог

Критерии C10 выполнены. Dashboard и Mimic используют точные опубликованные ревизии и bounded runtime data, authoring сохраняет заданный lifecycle, kiosk корректно работает при offline/reconnect/revoke, а physical commands остаются запрещены.
