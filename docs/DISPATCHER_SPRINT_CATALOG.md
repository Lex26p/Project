# Dispatcher — каталог execution-спринтов

**Статус:** исполняемое расписание  
**Дата:** 18 июля 2026 года  
**Основание:** `./DISPATCHER_IMPLEMENTATION_ROADMAP.md`

## 1. Общий контракт спринта

AI-разработчик выполняет только текущий sprint scope, самостоятельно выбирая внутренние классы, файлы и локальные implementation techniques. Каждый спринт завершается собираемым repository, green обязательными tests, обновлённым `./IMPLEMENTATION_STATE.md` и отсутствием незаявленных заглушек.

Изменение product scope, authority, public cross-module contract, security, persistence/transport baseline, process topology или command safety требует ADR. Необязательные улучшения и future scaffolding не входят в спринт.

`S00` выполнен настоящим documentation package. Реализация начинается с `S01`.

## 2. E01 — Platform Foundation

| ID | Вход / gate action | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S01` | `S00`; закрывает `IG-01` до создания solution | 1. Зафиксировать .NET toolchain и минимальную solution/repository structure.<br>2. Настроить warnings/analyzers/format/build policy.<br>3. Создать unit/integration test entry points.<br>4. Подготовить Windows и Linux CI/build evidence. | Clean checkout собирается и тестируется на Windows x64 и Linux x64; production code не зависит от Windows-only behavior. | Domain features, DB, Server API, Web screens, C++, пустые future modules. |
| `S02` | `S01` | 1. Зафиксировать canonical identity, revision/version и result/error semantics.<br>2. Реализовать typed value/unit/quality/freshness и source/receive/processed time.<br>3. Ввести injected wall/monotonic clocks и distinct position/cursor concepts.<br>4. Закрыть `IG-02` тестами. | Unit/property tests подтверждают type safety, no implicit conversion, deterministic clocks и невозможность смешения positions. | Full domain model, wire schema, universal entity base/interface. |
| `S03` | `S02`; закрывает `IG-03` до persistence code | 1. Закрыть data classification и owner transaction boundaries.<br>2. Зафиксировать persistence ADR; default — PostgreSQL с module-owned schemas/contexts.<br>3. Настроить migrations и isolated integration DB lifecycle.<br>4. Проверить atomic owner transition + mandatory obligation и rollback. | Fresh DB поднимается последовательно всеми migrations; повторный запуск безопасен; чужой owner не записывается напрямую; integration tests воспроизводимы. | Полные schemas будущих модулей, generic repository, final historian scale, broker. |
| `S04` | `S02–S03`; закрывает `IG-04` до session ingress | 1. Реализовать минимальные user/device/workload session semantics и effective permission check.<br>2. Ввести operation identity/idempotency и audit admission nucleus.<br>3. Добавить environment-gated test identity без production bypass.<br>4. Подключить structured observability, health/readiness и minimal durable job substrate. | Anonymous/admin shortcut отсутствует; revoke/expiry блокируют mutation; audit/idempotency tests проходят; dev identity не запускается в production profile. | Full IAM provisioning/admin UI, production AuthN, generic workflow engine. |

## 3. E02 — Walking Skeleton

| ID | Зависит от | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S05` | `S01–S04` | 1. Реализовать deterministic Simulator scenario/source primitives.<br>2. Провести observation через minimal Core admission/current path.<br>3. Создать scoped snapshot/delta и canonical golden trace.<br>4. Проверить restart identity и injected clocks. | Одинаковые seed/config/input дают одинаковый semantic trace на Windows/Linux; Simulator не мутирует current напрямую. | Scheduler полного масштаба, DB history, Alarm, protocols, physical commands. |
| `S06` | `S05`; закрывает `IG-05` до transport code | 1. Реализовать authorized Server query и scoped realtime bootstrap.<br>2. Подключить один Blazor widget к snapshot+delta.<br>3. Реализовать gap/disconnect/resync и permission invalidation.<br>4. Добавить end-to-end smoke/fault test. | Только разрешённый point попадает в Web; gap вызывает resnapshot; polling не равен render cadence; весь repository остаётся green. | Dashboard editor, production IAM, History, protocol I/O, polished design. |

## 4. E02A — Personal Workspace

| ID | Вход / gate action | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S06A` | `S04`, `S06` | 1. Реализовать каноническую Web shell/navigation/header и route guards.<br>2. Развести Account/Person и реализовать `/home`, `/me`, `/users/{userId}`, profile settings/availability/privacy в утверждённом scope.<br>3. Добавить permission-filtered global search, favorites/recent и role/organization-assigned Home composition с ограниченными personal overrides.<br>4. Проверить direct links, Back/Forward, session expiry и viewer filtering. | Home остаётся самостоятельным Workspace, не Dashboard entity/runtime; personal routes доступны только по effective permissions; profile не выдаёт системные права; hidden labels/counts не раскрываются; preferences переживают restart. | Social network functions, полное наполнение Home данными ещё не реализованных модулей, full HR/IdP provisioning, cosmetic polish. |

## 5. E03 — Facility and Equipment

| ID | Зависит от | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S07` | `S06` | 1. Реализовать Location, physical/functional relations, Equipment и Point ownership.<br>2. Добавить module persistence/migrations и invariants.<br>3. Ввести permission scopes и audit для mutations.<br>4. Подготовить fixtures для нескольких локаций. | Stable identities, graph relations и point definitions переживают restart; scope isolation и optimistic concurrency проверены. | Protocol polling, discovery, maintenance asset conflation, universal graph engine. |
| `S08` | `S07` | 1. Реализовать permission-filtered Location tree/summary/detail/plan-context и Equipment registry/search/detail projections.<br>2. Соединить equipment detail с current snapshot/realtime.<br>3. Добавить canonical `/locations`, `/locations/{id}`, equipment routes и quality/freshness/connection presentation data.<br>4. Провести Web integration и metadata-leak tests. | Location/Equipment list/count/filter используют один permission scope; inaccessible objects не раскрываются; detail показывает last value без ложной актуальности. | Advanced map/plan editor, full device editor, History, polished responsive UI. |

## 6. E04 — Configuration and Device Staging

| ID | Зависит от | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S09` | `S07–S08` | 1. Реализовать scoped configuration draft/revision model.<br>2. Разделить save, validate, publish, distribute и activate states.<br>3. Добавить optimistic concurrency, dependency fingerprint и rollback-as-new-revision.<br>4. Зафиксировать audit/jobs и recovery tests. | Изменённая после validation revision не публикуется; runtime не видит draft; publication и activation не смешиваются. | Generic content framework, dashboard authoring, real protocol activation. |
| `S10` | `S09` | 1. Реализовать единый multi-row staging editor contract.<br>2. Добавить manual row, CSV parse/validation, per-row result и add-only default.<br>3. Реализовать copy quantity с optional Modbus Unit ID increment и templates.<br>4. Зафиксировать per-row Equipment→initial-configuration obligation, idempotency и recoverable/reconcilable intermediate state; добавить Modbus TCP/SNMP form data и write-only secrets. | `created` возвращается только после Equipment acceptance и принятой initial-configuration obligation; ошибка привязана к row/field; отсутствующая CSV row ничего не удаляет; сохранённый secret не возвращается; SNMP `public` применяется только как default новой формы. | Actual diagnostics/Modbus/SNMP I/O, IP auto-increment, deletion by import, secret in templates/audit, cross-owner direct transaction. |
| `S11` | `S09–S10` | 1. Сформировать immutable Simulator runtime manifest.<br>2. Реализовать receipt/validation/whole-scope activation.<br>3. Восстановить active revision после restart.<br>4. Проверить rejection, reorder, duplicate и rollback. | Old/new configuration не смешиваются; crash around activation даёт доказуемый active set; rollback создаёт новую revision. | Production protocol activation, automatic failover, command execution. |

## 7. E05 — Runtime and Simulator

| ID | Зависит от | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S12` | `S11` | 1. Реализовать bounded scheduling/poll attempt lifecycle.<br>2. Добавить source binding/session generations, timeout и stale-result fencing.<br>3. Завершить normalization, RuntimeCut и current/liveness semantics.<br>4. Проверить overlap/missed policy без hard-coded production limits. | Stale response не меняет current; unchanged observation обновляет liveness без fictitious value transition; ordering deterministic. | Protocol-specific retry, production numeric limits, Alarm DSL. |
| `S13` | `S12` | 1. Реализовать protected fact classification и source obligations.<br>2. Добавить checkpoints/gaps, bounded backpressure и faceted readiness.<br>3. Реализовать startup/recovery/drain order.<br>4. Проверить capacity exhaustion и explicit degraded continuity. | Current восстанавливается snapshot; protected gap не скрывается; user mutation fail closed без required evidence; queues bounded. | HA takeover, final retention, cross-scope synchronous rules. |
| `S14` | `S12–S13` | 1. Расширить golden/property/fault corpus Simulator.<br>2. Добавить burst, clock anomaly, restart и slow-consumer scenarios.<br>3. Провести representative Linux load/soak и profile hotspots.<br>4. Зафиксировать только evidence, не нормативные arbitrary limits. | Semantic parity Windows/Linux; deterministic oracle green; нет unbounded growth; найденные bottlenecks имеют evidence/ADR. | C++ extraction, real equipment, final capacity certification. |

## 8. E06 — History

| ID | Зависит от | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S15` | `S13`, `IG-03` | 1. Реализовать independent History sample/gap acceptance.<br>2. Развести RuntimeFactPosition и HistoryStreamPosition.<br>3. Добавить idempotent ingest, late/out-of-order provenance и recovery checkpoint.<br>4. Проверить crash/replay/conflict. | AP-HIS отделён от current/source acceptance; duplicate не создаёт второй sample; irrecoverable interval сохраняется как gap. | Web live-tail as history, global cursor, separate historian service. |
| `S16` | `S15` | 1. Реализовать authorized range query, quality/gaps и stable pagination.<br>2. Добавить versioned aggregation/resolution policy.<br>3. Подключить trends и live-tail handoff.<br>4. Квалифицировать retention/query/load для initial store. | Gap очищает live continuity и вызывает History requery; aggregate воспроизводим; retention не удаляет данные вне policy. | BI analytics, report export, final historian extraction. |

## 9. E07 — Events and Alarms

| ID | Зависит от | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S17` | `S12–S13` | 1. Реализовать versioned Alarm definitions для утверждённого nucleus.<br>2. Добавить local evaluation на post-RuntimeCut state.<br>3. Реализовать AlarmOccurrence с независимыми condition/ack/assignment/shelving/suppression facets.<br>4. Проверить hysteresis/timers/restart в принятом объёме. | Ровно один evaluator на scope/epoch; raw adapter не создаёт occurrence; restart не даёт silent clear/duplicate. | Full Alarm DSL, cross-scope synchronous rules, command side effects. |
| `S18` | `S17` | 1. Реализовать immutable Event Journal acceptance/position.<br>2. Построить Event Dispatcher query/filter/counters.<br>3. Добавить отдельную occurrence projection и realtime catch-up.<br>4. Проверить permission filtering и gap behavior. | Изменение acknowledgement не мутирует Event record; journal и occurrence versions не смешаны; counts не раскрывают hidden data. | Incident workspace, notification delivery, universal event entity. |
| `S19` | `S17–S18` | 1. Реализовать authorized acknowledge/assign/shelve и maintenance constraints.<br>2. Добавить idempotency/audit/expected-version behavior.<br>3. Провести alarm flood, recovery, action races и priority tests.<br>4. Интегрировать dashboard/equipment links. | Timeout остаётся unknown до reconciliation; protected Alarm traffic не теряется из-за telemetry flood; independent facets сохранены. | Physical command, incident transition as side effect, notification read=ack. |

## 10. E08 — Dashboards and Mimics

| ID | Зависит от | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S20` | `S06A`, `S08`, `S16`, `S19` | 1. Реализовать Dashboard, DashboardWindow, Widget и binding model.<br>2. Добавить immutable published runtime manifest и exact dependencies.<br>3. Реализовать dashboard catalog/favorites/recent/last accessible dashboard.<br>4. Проверить permission-filtered manifest и fallback после потери доступа. | `{screen}` не создаёт отдельную domain entity; runtime использует целую revision; inaccessible binding отсутствует во всех metadata; Dashboard не подменяет Home workspace. | Editor canvas details, SVG manipulation, general responsive redesign. |
| `S21` | `S20` | 1. Реализовать subscription manifest только для visible/authorized window bindings.<br>2. Подключить current/Alarm/History links и per-widget partial/stale states.<br>3. Добавить coalescing, hidden-tab policy, reconnect/resync.<br>4. Провести high-cardinality/slow-client tests. | Один dashboard не получает чужие metrics; slow client не влияет на Core/других users; protected transitions не coalesce silently. | Device poll mirrored to browser, full DOM redraw, history via RT. |
| `S22` | `S20–S21` | 1. Реализовать Dashboard/Mimic draft/validate/publish/rollback lifecycles.<br>2. Добавить bounded SVG intake, sanitization и binding validation.<br>3. Подключить editor permission/audit и publication impact.<br>4. Проверить malicious SVG, stale validation и atomic runtime switch. | SVG не исполняет untrusted script/external leakage; draft не влияет на runtime; published change closes old subscription generation. | Collaborative editing, pixel-perfect designer automation, arbitrary embedded HTML. |
| `S22A` | `S22` | 1. Реализовать core Web workflow Dashboard Editor: draft, windows/widgets/bindings, validate/save/publish.<br>2. Реализовать отдельный core workflow SVG Mimic Editor: content/elements, bindings/states, preview/validate/save/publish.<br>3. Сохранить global shell/navigation и явные unsaved/conflict states.<br>4. Провести route/permission/direct-link/editor acceptance. | Редакторы имеют отдельные маршруты и сущности; stale/unsaved state явно обрабатывается; publish использует server validation exact revision. | Collaborative editing, advanced vector authoring, pixel-perfect polish, arbitrary SVG scripting. |

## 11. E09 — Protocol Commissioning

| ID | Зависит от | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S23` | `S11–S14`; закрывает `IG-06`, фиксирует protocol-specific `DG-07` evidence plan | 1. Зафиксировать protocol source/diagnostic contract и process/security topology.<br>2. Реализовать отдельный от ASP.NET Server Core/runtime host с workload identity, secret resolution, bounded parser/I/O и lifecycle supervision.<br>3. Создать parity contract suite против Simulator source semantics.<br>4. Проверить, что Core-controlled adapter не владеет current/Alarm/commands отдельно. | Core/runtime host отключается без нарушения Server authority; raw secret отсутствует в Web/log/audit; hostile input bounded; `DG-07` ещё открыт. | Третий protocol-worker process без `IG-09`, Driver SDK, plugin ABI, specific Modbus/SNMP implementation, writes. |
| `S24` | `S23` | 1. Реализовать Modbus TCP read-only configuration и acquisition в non-production profile.<br>2. Добавить Unit ID/address/type/endian validation и partial response semantics.<br>3. Реализовать connection test/sample poll diagnostics.<br>4. Провести disconnect/retry/stale-generation tests. | Diagnostics не меняет current/Alarm и не блокирует valid apply; stale response fenced; write function codes absent. | IP auto-increment, vendor discovery, physical writes, production claim до `S26`. |
| `S25` | `S23` | 1. Реализовать утверждённый SNMP read-only profile и OID/value normalization в non-production profile.<br>2. Добавить timeout/error/quality mapping и bounded response parsing.<br>3. Реализовать connection test/sample poll diagnostics.<br>4. Проверить secret masking/replacement и restart. | New v2c form may default to `public`, persisted secret не читается обратно; SET absent; malformed response isolated. | Full SNMP ecosystem, trap ingestion без отдельного scope, physical writes, production claim до `S26`. |
| `S26` | `S24–S25` | 1. Провести staging→publish→activate→current→History/Alarm→Web end-to-end.<br>2. Добавить multi-device load, reconnect storm и process crash recovery.<br>3. Квалифицировать Linux protocol deployment и diagnostics.<br>4. Freeze read-only contracts и закрыть `DG-07` раздельно для Modbus TCP read-only и SNMP read-only. | Два protocol adapters проходят один semantic path; process crash явно деградирует scope и восстанавливается без duplicate authority; каждый protocol имеет собственный qualification record. | Additional protocols, automatic service extraction, writes. |

## 12. E10 — Notifications

| ID | Зависит от | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S27` | `S18–S19`, `S06A` | 1. Реализовать mandatory/personal policy composition и subscriptions.<br>2. Добавить schedule, quiet periods, absence/coverage и channel preferences.<br>3. Реализовать personal inbox/read state и source links.<br>4. Проверить permission/coverage invariants. | Personal setting не ослабляет mandatory route; inbox read не подтверждает Alarm; source link повторно authorizes. | Provider delivery, full HR calendar integration, generic rules engine. |
| `S28` | `S27`; закрывает `IG-13` до provider code | 1. Зафиксировать initial SMTP production adapter либо ADR-equivalent provider.<br>2. Реализовать delivery obligations, provider attempts, retry/escalation и terminal outcomes.<br>3. Добавить channel test, substitution acceptance и realtime inbox counters.<br>4. Провести outage/backlog/restart/secret tests. | Provider timeout не меняет source workflow; duplicate attempt не создаёт duplicate accepted delivery; SMTP integration квалифицирована в controlled environment; secrets scrubbed. | Дополнительные providers, arbitrary SLO constants, alarm ownership. |

## 13. E11 — Incident and My Work Nucleus

| ID | Зависит от | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S29` | `S18–S19`, `S06A` | 1. Реализовать Incident identity/summary/coordinator/source link и create/link commands.<br>2. Реализовать My Work как projection назначений owner modules.<br>3. Добавить accept/transfer/return transitions только утверждённого task scope.<br>4. Проверить независимость Alarm/Incident/Task и permissions. | Incident creation не подтверждает Alarm; task transition не мутирует source без explicit contract; projection rebuildable. | Full crisis workspace, comments/SLA/participants, generic workflow/task owner. |

## 14. E12 — Maintenance

| ID | Зависит от | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S30` | `S07–S08`, `S04` | 1. Реализовать independent MaintenanceAsset с optional Equipment link.<br>2. Зафиксировать approved read-only plan nucleus, recurrence/forecast semantics и calendar queries.<br>3. Реализовать asset mutations, revision/audit и link/unlink.<br>4. Допускать полный plan CRUD только после `IG-11`. | Asset без telemetry полностью поддерживается; forecast не является WorkOrder; link/unlink не переписывает identity/history; provisional plan mutations отсутствуют. | Full plan CRUD/designer, resource optimization, contractor/mobile portal. |
| `S31` | `S30` | 1. Реализовать Request, Defect и WorkOrder approved lifecycle.<br>2. Добавить assignment, checklist, safety fields и acceptance.<br>3. Связать Event→MaintenanceRequest отдельной command.<br>4. Подключить My Work projection и permissions. | Обязательный checklist блокирует acceptance; WorkOrder completion не подтверждает Alarm; transitions versioned/audited. | Unapproved pause/resume/close variants, inventory/procurement. |
| `S32` | `S30–S31` | 1. Реализовать scheduler materialization и restart/idempotency.<br>2. Добавить timeline/history и cross-links без shared writes.<br>3. Провести overdue/concurrency/recovery/load tests.<br>4. Freeze accepted maintenance nucleus. | Один forecast создаёт не более одного WorkOrder по policy; recovery не дублирует; source owners остаются независимыми. | Full CMMS replacement и provisional lifecycle. |

## 15. E13 — Terminals and Kiosk

| ID | Зависит от | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S33` | `S04`, `S20–S22A`; закрывает `IG-12` до production identity | 1. Зафиксировать trusted terminal credential/enrollment decision.<br>2. Реализовать challenge/approval и отдельную device identity.<br>3. Добавить terminal fleet, profile, content assignment, block/revoke и presence.<br>4. Проверить expiry/replay/revoke/recovery races. | Shared profile не объединяет identities; blocked/revoked terminal не получает content; query parameter не является identity; pairing отсутствует до gate evidence. | Hardware attestation vendor, certificate fleet automation, playlists. |
| `S34` | `S33`, `S21` | 1. Реализовать ограниченный kiosk shell без main header/navigation/Event Dispatcher.<br>2. Добавить assigned runtime, heartbeat/sync и offline policy.<br>3. Реализовать configurable employee PIN/re-auth only where policy requires.<br>4. Зафиксировать Wallboard command deny. | Kiosk видит только assigned content; no-PIN action attributed only to terminal; offline commands not queued; Wallboard never gets command capability. | General responsive app, personal account per tablet, physical commands. |

## 16. E14 — Administration and Platform Operations UI

| ID | Зависит от | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S35` | `S04` и owner modules; закрывает `IG-04P` до production login | 1. Зафиксировать и реализовать initial production AuthN/session issuance/refresh/expiry/revoke; safe default — secure local Dispatcher accounts.<br>2. Реализовать agreed account/role/group/scope/effective-permission administration и last-admin protection.<br>3. Добавить inherited settings/overrides и integration diagnostics boundary.<br>4. Провести login/recovery/impact-preview/metadata-leak tests. | Production login не использует test identity; admin-only enforced backend; role/session change invalidates access; secrets remain write-only. | Full enterprise provisioning/OIDC lifecycle, arbitrary integration adapters, user-page/account conflation. |
| `S36` | `S13`, `S18`, `S35` | 1. Реализовать platform health, data-quality issues и faceted readiness views.<br>2. Добавить immutable audit query/live tail в утверждённом объёме.<br>3. Связать diagnostics с owners без превращения health в building Alarm.<br>4. Проверить audit gaps, permissions и overload. | Audit не редактируется через UI; health/data quality/Alarm distinct; hidden scope не раскрывается counters/logs. | External SIEM/BI, audit as generic event store. |

## 17. E15 — Command Lifecycle

| ID | Зависит от | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S37` | `S04`, `S11–S14`, `S19`, `S35`; закрывает `IG-07`/`AR-08/DG-05` до command code | 1. Зафиксировать command security decisions для Simulator-only path.<br>2. Реализовать time-bounded ControlLease и optional step-up policy.<br>3. Реализовать prepare/preflight intent с exact target/config/version.<br>4. Проверить revoke/expiry/stale/quality/blocking races. | Lease не заменяет permission; stale intent/active revision rejected; History mode denies command; no physical executor exists. | Protocol writes, generic command scripting, offline replay. |
| `S38` | `S37` | 1. Реализовать Simulator CommandExecution owner lifecycle.<br>2. Добавить accepted/rejected/progress/unknown/reconcile и idempotency.<br>3. Подключить audit/realtime/Web confirmation flow.<br>4. Провести timeout/restart/duplicate/security tests. | Timeout не становится success/failure; same identity reconciles previous result; new session cannot reuse identity; physical effect impossible. | Physical commands, scheduled scenarios, life-safety control. |

## 18. E15B — Physical Commands, conditional

| ID | Зависит от | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S39` | User-authorized `IG-08`, `S38`, `AR-08/DG-05`, applicable protocol-specific `DG-07` и закрытый target/environment pre-production operations evidence slice; full `DG-08` остаётся открыт | 1. Выбрать строго ограниченные protocol/target commands и isolated qualification profile/environment.<br>2. Реализовать qualification-only final Core gate и feedback/uncertainty contract.<br>3. Добавить per-command allowlist, disable switch и credential isolation.<br>4. Оставить production `physical-command-ready=false` и все остальные writes отсутствующими. | Только qualification profile и точный target получают command; stale authority/config/lease fail closed; disable switch проверен; production profile остаётся deny. | Production enablement, universal write API, inferred success, fire/life-safety writes. |
| `S40` | `S39` | 1. Провести device/simulator parity, timeout/partial effect и reconnect tests.<br>2. Выполнить security review, audit completeness и operator UX acceptance.<br>3. Проверить rollback/disable и incident response.<br>4. Выпустить protocol-specific qualification record. | Uncertain outcome остаётся visible/reconcilable; duplicate effect предотвращён либо явно ограничен device semantics; no unauthorized path; production enablement false. | Расширение command catalog или production enablement без `S43`. |

## 19. E16 — Operations and Final Acceptance

| ID | Зависит от | Цель и последовательные шаги | Acceptance | Explicit non-goals |
|---|---|---|---|---|
| `S41` | Все обязательные modules; закрывает bounded `IG-10`/AR-09 baseline до operations code и AR-10 после evidence | 1. Зафиксировать bounded production-topology candidate и operations decisions.<br>2. Реализовать Linux packaging/configuration/secrets/service supervision.<br>3. Добавить backup/restore и upgrade/rollback automation.<br>4. На полученном evidence консолидировать AR-10 и runbooks до `S42`. | Clean Linux environment installs/starts/stops/upgrades/restores по runbook; final topology имеет evidence/ADR; no development identity/secret defaults. | Cluster/HA без отдельного requirement, Kubernetes by default. |
| `S42` | `S41` | 1. Выполнить system security, fault, crash/recovery и overload matrix.<br>2. Провести reconnect/alarm flood/history query/job/provider/protocol contention tests.<br>3. Проверить observability, bounded resources и degraded modes.<br>4. Исправить только release blockers. | Нет silent data/authority loss; critical traffic сохраняет priority; backup restore и restart meet accepted evidence profile. | Новые функции, cosmetic redesign, arbitrary performance promises. |
| `S43` | `S42`; `S40` если physical scope авторизован | 1. Выполнить end-to-end acceptance по согласованному scope.<br>2. Freeze contracts/configuration/migrations и release notes.<br>3. Закрыть `DG-08`; при отдельно подтверждённом physical scope принять final production enablement, иначе сохранить deny.<br>4. Зафиксировать limitations/backlog и передать production baseline. | Все release-blocking criteria green; Linux artifact воспроизводим; rollback/support проверены; `DG-08` имеет final evidence record; physical writes enabled только после всех gates и explicit final decision; `IMPLEMENTATION_STATE` закрыт. | Provisional capabilities и следующий release. |

## 20. Правило перехода

Следующий спринт начинается после acceptance текущего и commit с обновлённым `./IMPLEMENTATION_STATE.md`. Допустимое исключение — явно указанная параллельная работа из roadmap, не меняющая незакрытый owner contract.
