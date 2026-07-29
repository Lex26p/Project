# C14 — Engineering staging, commissioning и durable diagnostics: итоговый отчёт

## 1. Результат

Реализован законченный Web-сценарий подготовки Modbus TCP и SNMP v2c устройств:

1. Инженер создаёт и редактирует manual staging rows либо загружает CSV.
2. Server выполняет authoritative parse/validation и возвращает field/per-row outcomes.
3. Copy сохраняет host, применяет bounded Unit ID strategy и не копирует secret.
4. Template не содержит identity, host, Modbus Unit ID или secret.
5. Create, update и skip применяются только явно; update требует отдельного administer permission и явной авторизации.
6. Connection test и sample poll ставятся Server в durable queue, исполняются RuntimeHost и читаются Web после повторного входа/refresh.
7. Applied rows включаются в новую whole-scope configuration revision, после чего доступны save, validate, publish и наблюдение activation.

Web не хранит собственную копию staging/configuration state и не выполняет protocol I/O.

## 2. API operations

Добавлены same-origin операции:

- `GET /api/equipment-staging`;
- `PUT /api/equipment-staging/{rowId}`;
- `POST /api/equipment-staging/csv`;
- `POST /api/equipment-staging/{rowId}/copy`;
- `POST /api/equipment-staging/{rowId}/authorize-update`;
- `POST /api/equipment-staging/apply`;
- чтение, сохранение, применение и удаление staging templates;
- запуск, чтение и чтение последнего diagnostic job;
- чтение configuration scope;
- save-staging, validate и publish configuration revision.

Bulk apply возвращает независимый outcome для каждой строки. Невалидная или unsupported строка не становится другим protocol автоматически и не удаляет отсутствующие в CSV строки.

## 3. Durable diagnostic lifecycle

1. Server проверяет session и scope permission, фиксирует fingerprint строки и сохраняет job.
2. RuntimeHost workload claims job с bounded lease.
3. Worker строит одноисточниковый production Modbus/SNMP plan и использует существующие read-only source factories/controllers.
4. Результат сохраняется как sanitised outcome и bounded samples.
5. Job допускает не более трёх claims; poll interval и lease задаются RuntimeHost settings.
6. Изменение staging row меняет fingerprint, поэтому прежний result читается как stale.
7. Job и результат сохраняются в PostgreSQL и не теряются при пересоздании Server store или повторном входе Web.

Server только enqueue/read, RuntimeHost только claim/execute/complete.

## 4. Secret boundary

- Web принимает secret как write-only поле и не получает его обратно.
- Plaintext сразу превращается в зашифрованную запись и ссылку `db:*`.
- Staging row, template, configuration manifest, API response, audit и diagnostic result содержат только ссылку либо признак наличия secret.
- RuntimeHost разрешает `db:*` только под workload identity и очищает временные `char`/request buffers.
- Copy и template secret не переносят.
- Проверка PostgreSQL persistence подтвердила отсутствие тестового plaintext в secret, audit и job text.

## 5. Web и browser scenarios

Страница `/equipment/add` содержит:

- отдельные формы Modbus TCP и SNMP v2c;
- staging table, existing-device/action state и field/per-row errors;
- CSV upload;
- copy и templates;
- connection test/sample poll с current/stale fingerprint;
- save → validate → publish → activation status.

Browser E2E проверяет:

- manual Modbus и SNMP rows;
- invalid field и per-row error;
- CSV partial success;
- safe copy и template;
- diagnostic pending, success, timeout и stale;
- повторный вход после reload с сохранённым job;
- explicit update authorization;
- запрет publish до validate и успешные publish/activation.

## 6. Изменённые области

### Production

- `src/Dispatcher.Equipment`: commissioning contracts/tools, encrypted secret storage, staging/template/diagnostic persistence и migration v3;
- `src/Dispatcher.Server`: commissioning/configuration endpoints и orchestration service;
- `src/Dispatcher.RuntimeHost`: diagnostic worker, database secret resolver и optional runtime settings;
- `src/Dispatcher.Web`: commissioning API client, `/equipment/add` и ссылка из Equipment;
- обновлены lock-файлы зависимого project-reference graph.

### Tests

- `tests/Dispatcher.UnitTests/EquipmentStagingToolsTests.cs`;
- `tests/Dispatcher.UnitTests/RuntimeHostOptionsTests.cs`;
- `tests/Dispatcher.IntegrationTests/EquipmentCommissioningDiagnosticTests.cs`;
- `tests/Dispatcher.BrowserTests/C14EquipmentCommissioningBrowserTests.cs`;
- C14 fixture в `tests/Dispatcher.BrowserTests/BrowserScenario.cs`.

## 7. Проверки

- `dotnet restore Dispatcher.slnx --locked-mode` — успешно после штатного обновления lock-файлов для новой зависимости RuntimeHost → Equipment;
- `dotnet build Dispatcher.slnx --configuration Release --no-restore` — успешно, 0 warnings, 0 errors;
- целевые staging/RuntimeHost Unit — 9/9 успешно;
- полный Unit — 149/149 успешно;
- целевые staging/configuration/diagnostic/reconciliation Integration — 17/17 успешно;
- полный Integration — 139/139 успешно;
- C14 Browser E2E — 3/3 успешно.

Проверки выполнялись локально на Windows x64, с локальной PostgreSQL и loopback fake protocol peers, без Docker, Linux и Git-операций.

## 8. Ограничения

- Physical hardware acceptance и реальные Modbus/SNMP устройства относятся к C15.
- Automatic discovery и physical writes не реализовывались.
- SNMP v3, SET, GETBULK, walk, traps и provisional generic protocol designer остаются вне C14.

## 9. Итог

Критерии C14 выполнены. Engineering staging, explicit apply, configuration publication и durable RuntimeHost diagnostics образуют единый безопасный Web-сценарий; refresh/re-login, stale fingerprint, permission boundary, per-row outcomes и secret isolation проверены.
