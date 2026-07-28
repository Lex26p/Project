# C08 — Web shell, visual system и browser baseline: итоговый отчёт

## 1. Результат

Технический набор разрозненных Web-ссылок заменён устойчивой диспетчерской
оболочкой. Реализованы единая desktop shell, изолированный kiosk layout,
локальная visual system, session-aware navigation, client-only route context,
согласованные состояния интерфейса и отдельный Playwright browser corpus.

C08 завершает Web-фундамент, на котором последующие задачи C09+ могут добавлять
предметные страницы без повторной замены layout, navigation, session flow и
базовых визуальных примитивов.

Реализация не добавляет provisional routes и не переносит test fixtures в
production Web.

## 2. Карта shell-компонентов

### 2.1. Composition и layout

- `App.razor`
  - единый `Router`;
  - `ErrorBoundary` для ошибок rendering;
  - `FocusOnNavigate` для переноса keyboard focus на заголовок страницы;
  - оформленный `not-found` state внутри основной shell.
- `MainLayout.razor`
  - header;
  - primary navigation;
  - content area;
  - context panel;
  - skip link;
  - session и appearance controls.
- `KioskLayout.razor`
  - отдельная layout boundary;
  - отсутствие обычной navigation, поиска и session controls;
  - read-only presentation для назначенного терминала.

### 2.2. Navigation и context

- `WorkspaceNavigation.razor`
  - navigation загружается с Server;
  - пункты определяются effective capabilities;
  - loading, empty и offline states;
  - повторная загрузка при изменении session generation;
  - отмена устаревшего запроса;
  - очистка пунктов предыдущего пользователя после logout или invalidation.
- `WorkspaceContextPanel.razor`
  - отображает текущий раздел;
  - показывает facility/equipment/dashboard route context;
  - показывает default scope и количество разрешённых scopes;
  - показывает effective capability count.
- `ShellRouteContextState.cs`
  - разбирает только клиентский route;
  - не записывает выбор пользователя в backend;
  - не выдаёт route selection за фактическое состояние оборудования.
- `WorkspaceSearchBox.razor`
  - остаётся частью общей header composition и использует same-origin API.

### 2.3. Presentation и design system

- `ShellPresentationState.cs`
  - light/dark theme;
  - comfortable/compact density;
  - независимое изменение параметров;
  - безопасное восстановление известных значений.
- `ShellPreferenceControls.razor`
  - keyboard-accessible controls;
  - `aria-pressed`;
  - блокировка до завершения инициализации browser storage.
- `dispatcher-ui.js`
  - локальное сохранение presentation preferences;
  - раннее применение theme до запуска Blazor;
  - отсутствие remote runtime dependency.
- `app.css`
  - локальные design tokens;
  - light и dark palettes;
  - density tokens;
  - desktop и kiosk grids;
  - focus indicators;
  - компоненты states, badges, forms и panels;
  - viewport `1440×900` без критичного overflow.

### 2.4. Общие UI-примитивы

- `UiStatePanel.razor`
  - loading;
  - empty;
  - partial;
  - stale;
  - offline;
  - forbidden;
  - session expired;
  - not found;
  - error.
- `UiBadge.razor`
  - независимые категории:
    - state;
    - severity;
    - acknowledgement;
    - assignment;
    - quality;
    - freshness.
- `GuardedContent.razor`
  - route authorization не выводится из видимости navigation;
  - access проверяется Server endpoint;
  - fail-closed session expiry;
  - offline и forbidden states не разрушают layout.

## 3. Session-aware поведение

`IdentitySessionState` является единой client-side session boundary.

Реализовано:

- production login и bootstrap остаются same-origin;
- authorization header устанавливается только после успешного bootstrap;
- logout очищает client session даже при transport failure;
- `401` инвалидирует client session;
- navigation повторно загружается после login, refresh, logout и invalidation;
- старые capability-filtered пункты не сохраняются после смены пользователя;
- session-expired state предлагает повторный вход;
- исходный локальный route передаётся через `returnUrl`;
- внешние, protocol-relative, backslash и recursive-login URLs отклоняются;
- после успешного входа пользователь возвращается к безопасному локальному route.

## 4. Browser test layer

Добавлен отдельный проект:

`tests/Dispatcher.BrowserTests/Dispatcher.BrowserTests.csproj`

Он встроен в `Dispatcher.slnx` и использует `Microsoft.Playwright`.

### 4.1. Топология

```text
Dispatcher.BrowserTests process
├── dotnet publish Dispatcher.Server
├── production-published Dispatcher.Server process
│   └── реальные Blazor WebAssembly assets
└── Playwright Chromium
    └── новый browser context для каждого scenario
        └── test-only interception запросов /api/**
```

Server и Web bundle являются production artifacts. API interception существует
только в browser test assembly и не входит в production Web или Server.

Полный межпроцессный путь с настоящими PostgreSQL, production identity,
production login, RuntimeHost, HTTP и SignalR уже проверяется C07. C08 browser
corpus намеренно проверяет браузерное поведение отдельно и не дублирует
многоминутный process E2E в каждом UI-сценарии.

### 4.2. Reusable fixture

- Server один раз публикуется во временный каталог;
- выбирается свободный loopback port;
- Server запускается отдельным процессом;
- readiness проверяется bounded HTTP polling;
- Chromium запускается headless по умолчанию;
- каждый тест получает независимый browser context `1440×900`;
- тест может детерминированно инвалидировать session без ожидания таймера;
- stdout/stderr Server сохраняются для диагностики;
- после корпуса Server и Browser завершаются;
- временный publish directory удаляется;
- удаление ограничено безопасным prefix внутри системного temp.

## 5. Browser scenarios

Реализованы и приняты девять сценариев:

1. **Login и capability-filtered shell**
   - открывается production Web bundle;
   - выполняется login;
   - отображается desktop shell;
   - доступны разрешённые `Home` и `Current`;
   - недоступная administrative navigation отсутствует;
   - отображается профиль текущего оператора.

2. **Direct route и reload**
   - открывается `/login?returnUrl=/home`;
   - выполняется полноценный browser reload;
   - return route сохраняется;
   - после входа выполняется переход на `/home`.

3. **Forbidden**
   - переход на route без capability возвращает `403`;
   - отображается `Access denied`;
   - desktop shell остаётся стабильной.

4. **Session expiry**
   - после входа session детерминированно инвалидируется;
   - следующий guarded route получает `401`;
   - client session очищается;
   - navigation предыдущего пользователя исчезает;
   - отображается `Session expired` и действие `Sign in`.

5. **Kiosk isolation**
   - открывается назначенный kiosk dashboard;
   - используется `KioskLayout`;
   - отсутствуют workspace navigation, search и обычные session controls;
   - отображается read-only terminal state.

6. **Desktop layout 1440×900**
   - document и shell не создают критичный horizontal/vertical overflow;
   - shell занимает viewport;
   - основная content area сохраняет рабочую ширину.

7. **Direct protected route и reload**
   - прямое открытие `/home` обслуживается Web fallback;
   - отсутствие client session отображается как стабильный `Session expired`;
   - reload сохраняет route и корректное session state.

8. **Theme и density persistence**
   - theme и density переключаются независимо;
   - выбранные значения отражаются в shell attributes;
   - значения восстанавливаются после полноценного browser reload.

9. **Keyboard focus и skip link**
   - клавиатурный focus имеет видимый outline;
   - skip link становится видимым при focus;
   - активация клавишей `Enter` переносит focus в основную content area.

## 6. Accessibility и keyboard baseline

Подтверждены следующие базовые механизмы:

- skip link к основной области;
- видимые focus styles;
- focus переносится на `h1` после navigation;
- appearance controls имеют `aria-label` и `aria-pressed`;
- state panels используют соответствующие `role` и `aria-live`;
- формы имеют связанные labels и стандартные autocomplete attributes;
- shell navigation и context areas имеют semantic landmarks;
- основные действия доступны как обычные links и buttons без mouse-only logic.

Полный WCAG-аудит, screen-reader matrix и специализированное accessibility
тестирование остаются отдельной последующей проверкой продукта.

## 7. Архитектурные ограничения, которые соблюдены

- Права не выводятся только из скрытой navigation.
- Route access повторно проверяется Server.
- Remote CDN, font, icon и script runtime dependencies не добавлены.
- Presentation preferences являются client-only настройками.
- Facility/equipment/dashboard route context не смешивается с backend state.
- Severity, acknowledgement, assignment, quality и freshness не сведены в один
  универсальный статус.
- Test fixtures не добавлены в production Web.
- Предметные страницы C09+ не реализованы внутри C08.
- Provisional routes ради заполнения меню не создавались.
- Kiosk composition не использует обычную administrative shell.

## 8. Изменённые области

### Production Web

- `src/Dispatcher.Web/App.razor`
- `src/Dispatcher.Web/GuardedContent.razor`
- `src/Dispatcher.Web/KioskLayout.razor`
- `src/Dispatcher.Web/MainLayout.razor`
- `src/Dispatcher.Web/Pages/Login.razor`
- `src/Dispatcher.Web/Program.cs`
- `src/Dispatcher.Web/ReturnUrlPolicy.cs`
- `src/Dispatcher.Web/ShellPreferenceControls.razor`
- `src/Dispatcher.Web/ShellPresentationState.cs`
- `src/Dispatcher.Web/ShellRouteContextState.cs`
- `src/Dispatcher.Web/UiBadge.razor`
- `src/Dispatcher.Web/UiPresentationModels.cs`
- `src/Dispatcher.Web/UiStatePanel.razor`
- `src/Dispatcher.Web/WorkspaceApiClient.cs`
- `src/Dispatcher.Web/WorkspaceContextPanel.razor`
- `src/Dispatcher.Web/WorkspaceNavigation.razor`
- `src/Dispatcher.Web/WorkspaceSearchBox.razor`
- `src/Dispatcher.Web/wwwroot/app.css`
- `src/Dispatcher.Web/wwwroot/dispatcher-ui.js`
- `src/Dispatcher.Web/wwwroot/index.html`

### Unit tests

- `tests/Dispatcher.UnitTests/ReturnUrlPolicyTests.cs`
- `tests/Dispatcher.UnitTests/ShellPresentationStateTests.cs`
- `tests/Dispatcher.UnitTests/ShellRouteContextStateTests.cs`
- `tests/Dispatcher.UnitTests/WorkspaceApiClientTests.cs`
- `tests/Dispatcher.UnitTests/WorkspaceRouteTests.cs`

### Browser test layer

- `tests/Dispatcher.BrowserTests/BrowserScenario.cs`
- `tests/Dispatcher.BrowserTests/BrowserServerFixture.cs`
- `tests/Dispatcher.BrowserTests/BrowserTestCollection.cs`
- `tests/Dispatcher.BrowserTests/Dispatcher.BrowserTests.csproj`
- `tests/Dispatcher.BrowserTests/WebShellBrowserTests.cs`

### Toolchain

- `Directory.Packages.props`
- `Dispatcher.slnx`

## 9. Проверки

Выполнены:

```powershell
dotnet restore .\Dispatcher.slnx
dotnet build .\Dispatcher.slnx -c Release --no-restore
dotnet test .\tests\Dispatcher.UnitTests\Dispatcher.UnitTests.csproj -c Release --no-build --no-restore
dotnet test .\tests\Dispatcher.IntegrationTests\Dispatcher.IntegrationTests.csproj -c Release --no-build --no-restore
dotnet test .\tests\Dispatcher.BrowserTests\Dispatcher.BrowserTests.csproj -c Release --no-build --no-restore
```

Результат контрольной проверки:

- restore успешен;
- Release build успешен;
- Unit: 136 успешно, 0 сбоев, 0 пропущено;
- Integration: 120 успешно, 0 сбоев, 0 пропущено;
- Browser: 9 успешно, 0 сбоев, 0 пропущено;
- всего: 265 тестов успешно.

Отдельно browser corpus был принят на Windows с Chromium:

- 9 успешно;
- 0 сбоев;
- 0 пропущено;
- длительность контрольного запуска — 20 секунд.

## 10. Сознательно отложенные UI-области

C08 создаёт shell foundation, но не реализует предметное содержание следующих
задач:

- полноценные Current и Equipment screens;
- Event и Alarm operational views;
- Dashboard и mimic authoring;
- Maintenance и commissioning workflows;
- Incident и My Work workplace;
- notification inbox и delivery administration;
- mobile technician workplace;
- SVG graphical editor;
- полный responsive/mobile layout;
- полный accessibility certification;
- cross-browser matrix за пределами принятого Chromium baseline.

Эти области должны использовать готовые shell, state и badge boundaries, а не
создавать параллельные layout и session flows.

## 11. Итог

Все критерии C08 выполнены:

- стабильная desktop shell реализована;
- capability-filtered navigation реализована;
- client-only context boundary реализована;
- обязательные UI states реализованы;
- независимые badge categories реализованы;
- light/dark theme и density controls работают и сохраняются;
- keyboard/accessibility baseline добавлен;
- kiosk изолирован от обычной shell;
- browser project и reusable fixture встроены в solution;
- все обязательные browser smoke scenarios проходят;
- полная контрольная валидация проходит: 265/265.

C08 переводится в `Complete`. C09 и C10 переводятся в `Ready`, поскольку их
runtime/current prerequisites и Web shell теперь завершены.
