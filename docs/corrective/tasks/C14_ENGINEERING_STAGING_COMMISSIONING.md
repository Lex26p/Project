# C14 — Engineering staging, commissioning и durable diagnostics

## 1. Назначение

Дать инженеру законченный безопасный Web-сценарий подготовки Modbus/SNMP устройств, проверки и публикации конфигурации.

## 2. Архитектурный контекст

Прочитать:

- `WEB_INTERFACE_SPECIFICATION.md`, разделы 8, 16.5–16.8, 28.5, 36.10 и 37;
- `WEB_BACKEND_API_REQUIREMENTS.md`, разделы 4, 5.4, 8 и 9;
- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 7, 13 и 14;
- `TASK_EXECUTION_RULES.md`.

## 3. Отправные файлы

- `src/Dispatcher.Server/EquipmentStagingService.cs`
- `src/Dispatcher.Equipment/EquipmentStagingModels.cs`
- `src/Dispatcher.Equipment/EquipmentStagingStore.cs`
- `src/Dispatcher.Equipment/EquipmentStagingTools.cs`
- `src/Dispatcher.Configuration/ConfigurationService.cs`
- workload/diagnostic contracts C11–C13
- `src/Dispatcher.Web/Pages/Equipment.razor`
- `src/Dispatcher.Web/Pages/EquipmentDetail.razor`
- `src/Dispatcher.Web/EditorApiClient.cs`
- `src/Dispatcher.Web/EditorWorkflowState.cs`
- `tests/Dispatcher.IntegrationTests/EquipmentStagingTests.cs`
- `tests/Dispatcher.UnitTests/EquipmentStagingToolsTests.cs`
- `tests/Dispatcher.IntegrationTests/ConfigurationRevisionTests.cs`

Остальные файлы изучать по необходимости.

## 4. Целевой результат

Инженер может:

- создать/редактировать staging rows вручную;
- загрузить CSV с server-authoritative parse/validation;
- копировать строки с безопасной unit-ID strategy;
- применять шаблоны без secret;
- видеть field/per-row errors и existing-device match;
- запускать connection test и sample poll как durable jobs;
- видеть sanitized result и stale fingerprint;
- применить только явные create/update/skip actions;
- сохранить, validate, publish и наблюдать workload activation revision.

## 5. Объём реализации

- Завершить Server endpoints/contracts для staging/configuration/diagnostic jobs.
- Использовать существующие stores/services, не дублировать их state в Web.
- Diagnostic job: Server authorizes/enqueues, RuntimeHost claims/executes, Server reads result.
- Secret вводится write-only и превращается в secret reference; plaintext не возвращается.
- Web forms отдельно представляют Modbus и SNMP fields.
- Connection test/sample poll информационны и привязаны к точному fingerprint.
- Bulk apply возвращает per-row outcome.
- Browser E2E:
  - manual Modbus/SNMP rows;
  - invalid fields;
  - CSV partial errors;
  - copy/template;
  - diagnostic pending/success/timeout/stale;
  - publish/activation/rejection;
  - explicit update authorization.

## 6. Архитектурные требования

- Отсутствующая CSV-строка ничего не удаляет.
- Неявный upsert запрещён.
- Host не меняется при copy.
- Secret не входит в template, response или audit.
- Web diagnostic не выполняет protocol I/O.
- Job bounded, cancellable и durable.
- Unsupported protocol не преобразуется автоматически.

## 7. Критерии приёмки

- Полный staging → validate → publish → activation работает для Simulator/fake Modbus/fake SNMP.
- Diagnostic исполняется RuntimeHost.
- Refresh страницы не теряет job.
- Изменение row делает старый result stale.
- Permission и per-row results проверены.
- Browser и existing staging/configuration tests проходят.

## 8. За пределами задания

- Automatic discovery.
- Physical writes.
- Реальная hardware acceptance C15.
- Provisional generic protocol designer.

## 9. Итоговый отчёт

Указать API operations, durable job lifecycle, secret handling и browser scenarios.
