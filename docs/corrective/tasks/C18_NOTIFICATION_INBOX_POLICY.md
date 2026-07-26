# C18 — Notification inbox, policy и delivery visibility

## 1. Назначение

Довести существующий Notifications backend до законченного пользовательского контура личного inbox, effective policy и безопасной информации о доставке.

## 2. Архитектурный контекст

Прочитать:

- `WEB_INTERFACE_SPECIFICATION.md`, раздел 19;
- `WEB_BACKEND_API_REQUIREMENTS.md`, раздел 5.9;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- `src/Dispatcher.Notifications/NotificationModels.cs`
- `src/Dispatcher.Notifications/NotificationStore.cs`
- `src/Dispatcher.Notifications/NotificationService.cs`
- `src/Dispatcher.Notifications/NotificationPolicyComposer.cs`
- `src/Dispatcher.Notifications/NotificationDeliveryModels.cs`
- `src/Dispatcher.Notifications/NotificationDeliveryStore.cs`
- `src/Dispatcher.Notifications/SmtpNotificationProvider.cs`
- `src/Dispatcher.Server/NotificationEndpoints.cs`
- `src/Dispatcher.Web/Program.cs`
- Web shell C08
- `tests/Dispatcher.IntegrationTests/NotificationAcceptanceTests.cs`
- `tests/Dispatcher.IntegrationTests/NotificationDeliveryTests.cs`

Остальные файлы изучать по необходимости.

## 4. Целевой результат

Пользователь может:

- видеть personal inbox и unread counter;
- отмечать одно/все сообщения прочитанными;
- переходить к доступному source;
- видеть sanitised delivery detail;
- видеть effective mandatory + personal policy;
- управлять только разрешёнными personal subscriptions/schedule/channels;
- наблюдать test/delivery job status.

## 5. Объём реализации

- Дополнить существующие Server endpoints стабильными query/command contracts.
- Web inbox/header counter/policy/subscription/schedule/channel screens.
- Realtime либо bounded feed unread/delivery changes.
- Сохранить provider-safe error model и retry state.
- SMTP test использует secret reference и durable job.
- Browser/integration tests:
  - inbox/unread;
  - source permission filtering;
  - mandatory rule cannot be disabled;
  - personal rule extension;
  - quiet hours boundary;
  - provider failure/retry;
  - no coupling to Alarm acknowledgement.

## 6. Архитектурные требования

- Read/unread не является Alarm acknowledgement.
- Personal policy не ослабляет mandatory coverage.
- Critical route не отключается quiet hours/digest.
- Credential не возвращается в Web/audit.
- Provider failure не блокирует runtime current/alarm pipeline.

## 7. Критерии приёмки

- Inbox и counters согласованы.
- Permission-revoked source не раскрывается.
- Effective policy показывает contributing rules.
- Delivery failure sanitised и повторяем.
- Existing Notifications tests и browser scenarios проходят.

## 8. За пределами задания

- Новые внешние providers кроме существующего SMTP.
- Полная HR absence/substitution модель, если она отсутствует в домене.
- Push/mobile clients.

## 9. Итоговый отчёт

Указать реализованные policy boundaries, delivery states и secret evidence.

