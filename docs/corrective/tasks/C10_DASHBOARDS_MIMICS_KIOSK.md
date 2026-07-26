# C10 — Dashboards, SVG-мнемосхемы и kiosk

## 1. Назначение

Довести существующие dashboard/mimic/kiosk каркасы до связанного production Web-сценария на published current.

## 2. Архитектурный контекст

Прочитать:

- `WEB_INTERFACE_SPECIFICATION.md`, разделы 11–14, 26 и 36.3;
- `WEB_BACKEND_API_REQUIREMENTS.md`, разделы 5.5 и 5.13;
- `docs/ADR-009_TERMINAL_ENROLLMENT.md`;
- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 10–12 и 15;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- `src/Dispatcher.Server/DashboardEndpoints.cs`
- `src/Dispatcher.Server/DashboardSubscriptions.cs`
- `src/Dispatcher.Server/DashboardAuthoringService.cs`
- `src/Dispatcher.Server/DashboardAuthoringEndpoints.cs`
- `src/Dispatcher.Server/TerminalRuntimeEndpoints.cs`
- `src/Dispatcher.Dashboards/*`
- `src/Dispatcher.Terminals/*`
- `src/Dispatcher.Web/DashboardApiClient.cs`
- `src/Dispatcher.Web/DashboardRuntimeState.cs`
- `src/Dispatcher.Web/Pages/DashboardEditor.razor`
- `src/Dispatcher.Web/Pages/MimicEditor.razor`
- `src/Dispatcher.Web/Pages/Kiosk.razor`
- `src/Dispatcher.Web/CurrentValueWidget.razor`
- `src/Dispatcher.Web/HistoryTrendWidget.razor`
- Web shell C08 и current session C06

Остальные файлы изучать по необходимости.

## 4. Целевой результат

- Dashboard catalog открывает точную published revision и выбранный `windowId`.
- Widget runtime получает current/history/events через существующие server contracts.
- SVG mimic рендерится из sanitised published revision и применяет bindings без исполнения произвольного script/HTML.
- Draft → save → validate → publish → runtime lifecycle работает с optimistic concurrency.
- Editor показывает validation errors и publication impact.
- Kiosk получает content assignment из trusted terminal session, поддерживает reconnect/resync и не показывает authoring/admin UI.

## 5. Объём реализации

- Завершить недостающие API/Web contracts без объединения Dashboard и Mimic identities.
- Реализовать базовую библиотеку widgets, указанную Core UI specification.
- Обеспечить bounded subscription по видимым bindings.
- Довести layout/inspector/validation workflow существующих editors; полноценный arbitrary vector editor не требуется.
- Использовать безопасный SVG intake и published revision.
- Реализовать full-screen и kiosk layout.
- Browser E2E:
  - catalog/runtime/deep link;
  - current update;
  - history widget;
  - event indicator;
  - draft validation/publish;
  - unsafe SVG rejection;
  - kiosk assignment/revoke/offline-resync.

## 6. Архитектурные требования

- Runtime никогда не смешивает revisions.
- Draft save не меняет runtime.
- Dashboard и Mimic остаются отдельными сущностями.
- Hidden/offscreen bindings не создают неограниченную подписку.
- Kiosk wallboard effective command policy всегда deny.
- SVG не выполняет script, remote resource или event handler.

## 7. Критерии приёмки

- Опубликованный dashboard работает на данных C05.
- Publish появляется в runtime только целой revision.
- Unsafe SVG отклоняется server-side.
- Kiosk использует trusted terminal identity, не query terminal ID.
- Realtime gap восстанавливается snapshot.
- Browser и существующие dashboard/terminal tests проходят.

## 8. За пределами задания

- Полноценный CAD/SVG editor.
- Playlists, если их schema остаётся provisional.
- Physical commands.

## 9. Итоговый отчёт

Описать supported widgets, revision lifecycle, SVG security и kiosk evidence.

