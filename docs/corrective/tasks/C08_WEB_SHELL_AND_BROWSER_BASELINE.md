# C08 — Web shell, visual system и browser baseline

## 1. Назначение

Заменить технический набор ссылок целостной диспетчерской оболочкой и создать браузерную тестовую основу для последующих Web-задач.

## 2. Архитектурный контекст

Прочитать:

- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, раздел 12;
- `WEB_INTERFACE_SPECIFICATION.md`, разделы 5–10, 29–32, 36 и 37;
- product concept — разделы о ролях и Web;
- `TASK_EXECUTION_RULES.md`.

HTML/CSS/JS prototype использовать как визуальный reference, если он передан вместе с проектом. Его fixtures и demo-поведение не переносить как production logic.

## 3. Отправные файлы

- `src/Dispatcher.Web/App.razor`
- `src/Dispatcher.Web/MainLayout.razor`
- `src/Dispatcher.Web/KioskLayout.razor`
- `src/Dispatcher.Web/WorkspaceNavigation.razor`
- `src/Dispatcher.Web/WorkspaceSearchBox.razor`
- `src/Dispatcher.Web/GuardedContent.razor`
- `src/Dispatcher.Web/Pages/Home.razor`
- `src/Dispatcher.Web/Pages/Login.razor`
- `src/Dispatcher.Web/wwwroot/index.html`
- все существующие Web routes — для построения navigation map
- same-origin/session composition C06

Остальные файлы изучать по необходимости.

## 4. Целевой результат

Web имеет:

- стабильную desktop shell: header, primary navigation, content area и context panel;
- role/capability-filtered navigation;
- понятный выбранный facility/equipment/dashboard context;
- loading, empty, partial, stale, offline, forbidden и not-found states;
- визуально независимые severity, acknowledgement, assignment, quality и freshness;
- рабочие light/dark theme и density controls в согласованной границе;
- keyboard focus и базовую accessibility;
- kiosk layout без administrative navigation;
- browser test project и reusable authenticated test fixture.

## 5. Объём реализации

- Ввести локальные design tokens и компонентные стили без зависимости от remote CDN.
- Перестроить shell и navigation по спецификации.
- Добавить единые UI components для state/error/badge/table/filter/drawer patterns, где это уменьшает дублирование.
- Не смешивать client-only selection с backend state.
- Добавить error boundary и session-expired flow.
- Добавить .NET Playwright либо эквивалентный локальный browser harness, встроенный в solution/toolchain.
- Browser smoke:
  - login;
  - shell/navigation;
  - direct route/reload;
  - forbidden;
  - session expiry;
  - kiosk isolation;
  - 1440×900 layout без критичного overflow.

## 6. Архитектурные требования

- UI не получает права только из скрытой навигации.
- Не добавлять external font/icon/script runtime dependency.
- Не превращать все состояния в один цвет/status.
- Не переносить demo fixtures в production.
- Не реализовывать предметные страницы C09+ внутри этой задачи.
- Не добавлять provisional routes только ради заполнения меню.

## 7. Критерии приёмки

- Основные маршруты открываются внутри одной shell.
- Navigation соответствует effective capabilities.
- Keyboard focus видим, основные действия доступны без мыши.
- Loading/error/offline не вызывают layout collapse.
- Kiosk не показывает обычную shell.
- Browser smoke воспроизводим на Windows 11.

## 8. За пределами задания

- Полное содержание Event/Dashboard/Maintenance screens.
- Mobile technician workplace.
- Графический редактор SVG.

## 9. Итоговый отчёт

Привести карту shell components, browser scenarios и список сознательно отложенных UI areas.

