# Dispatcher — состояние реализации

**Обновлено:** 20 июля 2026 года
**Статус программы:** `S25` реализован и проверен на Windows x64; остановлено перед `S26`

**Последний завершённый пакет:** `S25` — SNMP v2c read-only non-production configuration/acquisition, canonical OID/numeric normalization, bounded BER/UDP, timeout/error/quality mapping, connection/sample diagnostics и reference-only community replacement/restart (working tree; commit выполняет пользователь)

## Следующая работа

`S26` — staging→publish→activate→current→History/Alarm→Web для Modbus TCP/SNMP read-only, multi-device/reconnect/process-crash recovery, заявленная deployment qualification и отдельные protocol qualification records из `./DISPATCHER_SPRINT_CATALOG.md`. В текущей работе не начат.

Windows x64 evidence для `S25`: solution build — 0 warnings/0 errors; 86 unit + 57 integration tests green. Integration tests использовали отдельный временный PostgreSQL cluster без Docker.

Linux x64 build/test/load не выполнялись по прямому указанию пользователя; соответствующее evidence `IG-01` и platform parity `S14` остаются открытыми и не заявляются.

## Зафиксировано

- Product/Web/API sources выбраны и не пересозданы.
- `ADR-001` принят: Core/runtime — C#/.NET first; authority неизменна.
- Implementation model: foundation → walking skeleton → module-by-module production-like completion.
- Web/Kiosk/Wallboard → Server only.
- Server не выполняет protocol I/O.
- Realtime не является History.
- Production physical writes hard-deny; qualification также отсутствует до отдельной user authorization.
- Provisional product capabilities не получают выдуманные contracts.

## Gate status

| Gate | Статус | Требуемый момент |
|---|---|---|
| `IG-01` Toolchain | Partial | .NET 10 foundation и Windows x64 locked restore/build/tests green; Linux x64 не проверен по указанию пользователя |
| `IG-02` Semantic contracts | Closed | `ADR-003`; unit/property-style tests подтверждают distinct IDs, values, times, revisions, positions/cursors и deterministic injected clocks |
| `IG-03` Data/persistence | Closed | `ADR-004`; PostgreSQL fresh/repeat migrations, checksum, rollback, owner-role isolation, atomic obligation и dump/restore tests green |
| `IG-04` Session/security nucleus | Closed | `ADR-005`; anonymous/revoke/expiry/permission denial, gated test identity, audit/idempotency и durable job tests green |
| `IG-04P` Production AuthN | Open | Закрыть до production login в `S35` |
| `IG-05` Web/realtime transport | Closed | `ADR-006`; authorized HTTP snapshot и scoped SignalR bootstrap/delta, gap/reconnect/resnapshot, permission invalidation и slow-consumer/render-cadence tests green |
| `IG-06` Protocol isolation | Closed | Отдельный non-ASP.NET Core/runtime process, exact workload identity, reference-only secret resolution, bounded I/O/parser surface, lifecycle drain/isolation и Simulator parity contract tests green |
| `IG-07` Command security | Open | Закрыть до `S37` |
| `IG-08` Physical write | Not authorized / deny | Только решение пользователя и scoped qualification gates перед `S39`; full `DG-08` и final production enablement не раньше `S43` |
| `IG-09` Extraction | Not justified / remain in current deployable | Открывать только по evidence и ADR |
| `IG-10` Production operations | Open | Bounded baseline до operations code в `S41`; final AR-10 consolidation по evidence до `S42` |
| `IG-11` Product maturity | Not authorized for provisional scope | Только отдельное решение пользователя |
| `IG-12` Terminal enrollment | Open | Закрыть до production identity в `S33` |
| `IG-13` Notification provider | Open | Закрыть до provider code в `S28`; default SMTP |

## `DG-07` evidence plan — Open

`S23` фиксирует план, но не закрывает protocol-specific `DG-07`:

- `S24 / Modbus TCP read-only` — evidence collected: configuration validation, partial-response semantics, diagnostic/current isolation, disconnect/retry/stale-generation fencing и exact supported function-code surface `FC03/FC04` без write function codes. Production qualification ещё отсутствует до `S26`.
- `S25 / SNMP read-only` — evidence collected: exact v2c GET-only profile, canonical OID/numeric normalization, timeout/PDU/per-OID quality mapping, bounded malformed-response isolation, reference-only community masking/replacement/restart и exact request PDU surface без SET. Production qualification ещё отсутствует до `S26`.
- `S26 / separate qualification records`: для Modbus TCP read-only и SNMP read-only отдельно выполнить staging→publish→activate→current→History/Alarm→Web, multi-device/reconnect/process-crash recovery и заявленную deployment qualification. Только после этого `DG-07` может закрываться раздельно для двух profiles.

## Stable

- Архитектурные инварианты `AR-01` — `AR-06` в актуализированной редакции.
- `ADR-001` C#-first.
- `ADR-002`: .NET 10/`net10.0`, `.slnx`, central package versions и locked restore.
- `ADR-003`: canonical typed identity, revision/version, result/error, value/unit/quality/freshness, distinct timestamps, clocks и owner-scoped positions/cursors.
- `ADR-004`: PostgreSQL/Npgsql, module-owned schemas/roles/migration histories и transactional owner obligation.
- `ADR-005`: user/device/workload sessions, exact effective permissions, operation admission/idempotency/audit и environment-gated test identity.
- `ADR-006`: ASP.NET Core HTTP + SignalR, per-subscription opaque Web cursor, authorized point filtering и mandatory resnapshot после gap/reconnect/permission-set change.
- `ADR-007`: Core runtime current использует явно переданные point/delta capacities, atomic capacity failure и bounded FIFO retention; cursor старше retained delta либо checkpoint restore требует resnapshot.
- Platform nucleus использует structured activities/metrics, database readiness и lease-based durable jobs без workflow engine.
- Simulator генерирует deterministic observations по seed/config и не получает mutable Core current.
- Core владеет scope admission/current, scoped snapshot/delta и canonical golden trace; restart сохраняет configured identities.
- `Dispatcher.Protocols` задаёт только read-only transport, source normalization и отдельную diagnostics surface; contracts не содержат write/command API и не зависят от Server/Alarm.
- `Dispatcher.RuntimeHost` является отдельным от ASP.NET Server executable process: protocol acquisition проходит только через `RuntimeProcess` в Core ingress, а diagnostics не создаёт `RuntimeCut` и не меняет current/Alarm.
- Protocol workload identity проверяется при регистрации source; raw secret доступен только внутри short-lived zeroed lease, отсутствует в serializable contracts/Web boundary и runtime log messages.
- Protocol I/O ограничен explicit timeout/response bytes/concurrency, parser output — explicit observation capacity; lifecycle stop закрывает admission и дожидается bounded in-flight reads.
- `Dispatcher.Modbus` реализует только `NonProductionReadOnly` Modbus TCP profile с exact read function-code surface `FC03`/`FC04`; write functions/API отсутствуют.
- Modbus configuration валидирует bounded point/register capacities, Unit ID `0..255`, port, zero-based register address, signed/unsigned 16/32-bit type и explicit byte/word order.
- Modbus MBAP transport использует bounded response size и retry policy; partial protocol exception сохраняет valid observations, а failed point становится explicit `Bad/Stale` без отклонения valid cut.
- Modbus `ConnectionTest` и `SamplePoll` используют diagnostic surface без source-position/current/Alarm mutation; late acquisition после смены binding/session generation fenced до Core ingress.
- `Dispatcher.Snmp` реализует только `NonProductionV2cReadOnly` SNMP profile с exact GET request PDU surface; SET, traps и SNMP v3 отсутствуют.
- SNMP configuration хранит только community secret reference, canonical bounded OID и declared numeric type; raw community разрешается per-operation, очищается после request encoding и не входит в configuration/diagnostic/current contracts.
- Bounded BER parser проверяет v2c envelope, echoed community, request ID, GetResponse PDU, ordered OID set, value tags и response shape; malformed datagram изолируется без source-position advance.
- SNMP Integer32/Counter32/Gauge32/TimeTicks/Counter64 нормализуются в canonical runtime integer при проверенном range; PDU/per-OID errors становятся explicit `Bad/Stale`, timeout проходит bounded retry policy.
- SNMP `ConnectionTest` и `SamplePoll` не меняют source position/current/Alarm; замена community по прежней reference применяется на следующей операции и после recreation runtime source.
- Server выдаёт current snapshot/delta только после session/point authorization; скрытые point и их Core positions не попадают в Web.
- Один Blazor current-value widget использует snapshot+delta; no-change polling не запускает render, catch-up delta применяет несколько изменений перед одним render request.
- `MOD-WSP` разделяет Account и Person, владеет PostgreSQL schema/migrations для profile/preferences/Home/favorites/recent и пишет preference audit атомарно с mutation.
- Канонические `/home`, `/me`, `/users/{userId}`, `/search` и guarded `/current` используют Server-side effective permission checks; session expiry и direct links fail closed.
- Home остаётся independent Workspace: composition объединяет account/role/organization assignments с ограниченными personal overrides; недоступные labels/counts отсутствуют в Web payload.
- Profile availability/privacy, preferences, favorites и recent переживают restart; profile settings не меняют effective permissions.
- Shared canonical `PointId` находится в semantic layer; Equipment владеет point definitions, а Core продолжает владеть только runtime current.
- `MOD-FAC` владеет отдельным PostgreSQL schema: stable Location/scope identities, explicit physical containment и functional relations, scoped audit и cycle/scope invariants.
- `MOD-EQP` владеет отдельным PostgreSQL schema: Equipment identity/location reference и owned Point definitions без cross-owner writes или cross-schema FK.
- Facility/Equipment mutations используют exact scope permissions, expected versions и atomic owner audit; Facility graph mutations дополнительно сериализуются per-scope advisory lock.
- Multi-location fixtures подтверждают restart persistence, scope isolation, owner-role isolation и optimistic concurrency.
- Server формирует Location tree/summary/detail/plan-context и Equipment registry/search/detail только после единой Facility+Equipment scope authorization; недоступные scope, labels, counts и связанные объекты не попадают в payload.
- Канонические Web-маршруты `/locations`, `/locations/{locationId}`, `/equipment`, `/equipment/{equipmentId}` используют только Server API и повторную проверку доступа для direct links.
- Equipment detail связывает point definitions с отдельно разрешённым Core current snapshot/realtime, явно передаёт quality/freshness и три timestamp; отсутствие protocol evidence представляется как `ConnectionStatus=Unknown`, а не как ложный `Connected`.
- `MOD-RLS` владеет отдельным PostgreSQL schema для scope-owned configuration revisions, release heads, distribution obligations и mutation audit без cross-owner writes.
- Configuration lifecycle разделяет save, validate, publish, distribute и activation acknowledgement; draft не попадает в desired release, а publish не изменяет active revision.
- Validation привязана к manifest/dependency fingerprints и инвалидируется повторным save либо изменением dependencies; optimistic concurrency и per-scope serialization закрывают revision races.
- Publication атомарно создаёт lease-based distribution job; restart/lease expiry допускает безопасный reclaim, а завершение устаревшей delivery не продвигает текущий release head.
- Rollback копирует опубликованный snapshot в новую непроверенную revision и не перемещает published/active head до обычных validate/publish/distribute/activate переходов.
- Configuration manifest fingerprint использует детерминированную канонизацию JSON и остаётся стабильным после хранения в PostgreSQL `jsonb`.
- Simulator runtime владеет отдельным PostgreSQL schema с immutable whole-scope manifests, строгим receipt-порядком, явными validated/rejected states и атомарным active revision pointer/generation.
- Duplicate receipt/activation идемпотентны; crash перед activation commit сохраняет прежний доказуемый active set, restart восстанавливает его из durable pointer, а rollback активируется только как новая revision.
- Server применяет только distributed desired release целиком и подтверждает Configuration activation после успешного Simulator commit; production protocol activation, automatic failover и command execution отсутствуют.
- Poll scheduling ограничен явно переданными `maxBindings`, `maxInFlight` и timeout; overlap пропускается с явным missed outcome, capacity exhaustion и timeout не создают скрытый poll result, production numeric limits не зафиксированы.
- Source binding generation связан с active manifest generation, source session generation отделён от него; rebind атомарно инвалидирует in-flight attempt, а Scheduler и Core независимо отсекают stale completion/cut.
- Нормализованный `RuntimeCut` содержит один source binding, уникальные point/source positions и детерминированный порядок point; Core применяет cut атомарно и не допускает частичный position advance.
- Current position продвигается только при изменении value/unit/quality/freshness либо generation provenance; неизменное observation продвигает отдельную source liveness position без фиктивного current delta.
- Runtime facts классифицированы явно: current/liveness checkpoint rebuildable, а accepted source cut и source gap являются protected facts.
- Core/runtime владеет отдельным PostgreSQL schema с immutable source obligations, monotonic obligation positions и rebuildable checkpoint; admission становится durable до помещения cut в bounded memory queue.
- Startup сначала восстанавливает checkpoint, затем replay pending protected obligations по owner position; drain сначала закрывает admission, последовательно checkpoint-ит очередь и только затем переходит в stopped state.
- Bounded ingress capacity задаётся вызывающей стороной; exhaustion создаёт durable visible gap, закрывает дальнейший admission и сохраняет прежний current в explicit degraded read-only continuity без скрытой потери.
- Faceted readiness разделяет persistence, recovery, protected continuity, admission и queue capacity; user mutation fail closed без полного evidence, persistence failure переводит runtime host в `Faulted`.
- Manual row, bounded CSV parser, copy quantity и secret-free templates используют единый Equipment staging row contract; CSV остаётся add-only и возвращает ошибки по row/field.
- Copy сохраняет host/IP и опционально увеличивает только Modbus Unit ID; write-only secrets не копируются и не входят в templates, snapshots либо audit.
- Modbus TCP и SNMP form data сохраняются как configuration obligations без diagnostics/protocol I/O; SNMP `public` существует только в factory новой формы.
- Server координирует Equipment acceptance и initial-configuration obligation последовательными owner-вызовами без cross-owner transaction; `created` возвращается только после обоих acceptance points.
- Durable staging intermediate state и idempotent owner operations позволяют reconcile после сбоя между Equipment и Configuration; повтор не создаёт дубликаты, а конфликт row ID fail closed.
- Staging secret material хранится только в защищённом виде; публичные staging/obligation snapshots раскрывают лишь `HasSecret`, а audit не содержит form data или secret.
- Integration DB lifecycle использует отдельный временный PostgreSQL cluster без Docker и не изменяет рабочую БД.
- Расширенный Simulator corpus фиксирует deterministic burst golden, 32-seed property runs, mass timeout, wall-clock regression, restart/resnapshot, slow-consumer gap и atomic point-capacity failure; workload profile сохраняет bounded point/current-delta state без нормативных capacity обещаний.
- `MOD-HIS` владеет отдельной PostgreSQL schema для immutable sample/gap acceptance; `RuntimeFactPosition` и `HistoryStreamPosition` являются разными типами и продвигаются в разных acceptance orders.
- History ingest использует exact fact fingerprint: совместимый replay возвращает прежние records без duplicate, а тот же runtime position с другим content fail closed как conflict.
- Late/out-of-order provenance сохраняется на sample; irrecoverable source interval принимается отдельным durable gap, а contiguous recovery checkpoint не перескакивает через отсутствующий runtime fact и безопасно догоняет после reorder/crash replay.
- History range query требует одновременно History и point permission, возвращает quality/freshness и gaps, а фиксированный upper bound сохраняет стабильную pagination при concurrent ingest.
- Versioned resolution policy `v1` детерминированно агрегирует count/average/min/max, worst quality/freshness и gap presence; Web trend продолжает live-tail через отдельный realtime snapshot и после realtime gap очищает continuity и повторно запрашивает History.
- History retention выполняется только по явной versioned policy, cutoff и bounded history position; immutable protection разрешает delete только внутри retention transaction. Initial query profile проверяет paged traversal 32 samples без production capacity claim.
- `MOD-ALM` владеет отдельной PostgreSQL schema для immutable contiguous definition epochs, единого serialized evaluation state на scope и durable `AlarmOccurrence` lifecycle.
- Alarm evaluator принимает только принятый `RuntimeCutAcceptance` с соответствующим post-cut current snapshot; raw adapter не имеет API создания occurrence, а stale/replayed evaluation position не создаёт второго перехода.
- У occurrence раздельно хранятся condition, acknowledgement, assignment, shelving и suppression facets с независимыми versions; local condition evaluation изменяет только condition facet.
- High/low threshold nucleus использует explicit hysteresis и durable raise/clear timers; pending/active state переживает restart без silent clear либо duplicate occurrence.
- `MOD-EVT` владеет отдельной PostgreSQL schema: immutable Event Journal получает независимую `EventJournalPosition`, а rebuildable occurrence projection — отдельную `OccurrenceProjectionVersion`.
- Alarm condition facet version принимается в Event Journal идемпотентно; изменение acknowledgement либо другого occurrence facet продвигает только projection и не мутирует ранее принятый Event record.
- Event Dispatcher query, filters и counters применяют session и point permissions до формирования rows/counts; hidden points не раскрываются ни payload, ни counters.
- Persistence-backed occurrence snapshot/catch-up использует bounded cursor window: stale/future cursor возвращает gap, hidden-only changes безопасно продвигают cursor без раскрытия payload, permission change инвалидирует realtime subscription.
- Alarm acknowledge, assign и shelve требуют отдельные effective permissions и point permission; trusted maintenance constraint может независимо запретить facet action либо ограничить shelving maintenance window.
- Каждое Alarm action использует subject-scoped idempotency key, exact expected facet version и atomic owner audit; concurrent race допускает ровно один facet advance, а replay не создаёт второй audit.
- Action timeout после commit остаётся `Unknown` до повторного запроса с тем же idempotency key; reconciliation возвращает durable replay и только затем догоняет Event occurrence projection.
- Alarm priority фиксируется definition/occurrence и переносится в immutable Event/projection; bounded flood corpus сохраняет все protected journal records и подтверждает priority filtering.
- Alarm source response передаёт stable Dashboard point-binding key и canonical Equipment route, сохраняя повторную authorization у целевого consumer.
- `MOD-DSH` владеет отдельной PostgreSQL schema для Dashboard catalog, immutable published revisions и dashboard-specific favorite/recent/last-accessible state; Home остаётся отдельным `MOD-WSP` workspace.
- Dashboard revision публикуется и восстанавливается только целиком; Window является каноническим экранным контрактом без отдельной `{screen}` domain entity, а exact dependencies детерминированно fingerprinted и полностью покрывают bindings.
- Server фильтрует catalog по Dashboard permission, а published manifest — по binding permission до формирования windows/widgets/bindings/dependencies; hidden binding отсутствует во всех выдаваемых metadata.
- При потере доступа last-accessible Dashboard заменяется первым разрешённым favorite/recent/catalog candidate и новый fallback сохраняется; недоступный Dashboard не раскрывается в catalog либо landing response.
- Dashboard subscription manifest строится только для явно visible windows уже permission-filtered published revision; отдельные limits на visible windows и bindings fail closed до создания consumer links.
- Current и Alarm bindings ссылаются на существующие scoped realtime boundaries, а History binding содержит exact source identity и использует только HTTP History endpoint; device polling не зеркалируется в Web и History не переносится в realtime.
- Web Dashboard runtime хранит client-local binding state и агрегирует его в `Ready`/`Partial`/`Stale` widget state; disconnect либо protected gap требует authorized resnapshot.
- Hidden tab прекращает current polling/render и coalesce только latest current per binding; Alarm protected transitions сохраняются по position без coalescing, а исчерпание bounded client buffer создаёт явный gap/resync вместо silent loss.
- Dashboard runtime state и capacity изолированы per client: медленный/hidden consumer не блокирует Core и не меняет cursor/state другого пользователя; production numeric limits остаются конфигурационными и не квалифицированы.
- Dashboard и Mimic являются отдельными authoring resources с optimistic draft version, immutable published revisions и отдельными `save`/`validate`/`publish`/`rollback` transitions; draft никогда не меняет runtime manifest.
- Editor save инвалидирует validation; validate фиксирует exact content/dependency fingerprints, stale revision не публикуется, а rollback копирует опубликованный snapshot в новую непроверенную revision без перемещения active pointer.
- Dashboard publish в одной owner transaction создаёт immutable whole revision, переключает runtime pointer и пишет editor audit; после commit все subscription leases прежней revision закрываются и требуют новый authorized manifest.
- Mimic SVG intake ограничен конфигурируемыми UTF-8 bytes/elements/attributes/value length, запрещает DTD, script, foreign/external namespaces, event handlers, `url(...)`, `javascript:`/`data:` и не принимает arbitrary HTML.
- Sanitized Mimic SVG требует exact equality объявленных и использованных binding identities, а dependencies полностью покрывают bindings; опубликованный Mimic content защищён PostgreSQL trigger от mutation.
- Dashboard/Mimic editor mutations используют отдельные resource/action permissions и атомарный owner audit с session/subject/permission/resulting version.
- Server предоставляет отдельные permission-checked `/api/dashboard-editor` и `/api/mimic-editor` contracts для read/save/validate/publish и sanitized Mimic preview; optimistic conflict/stale validation представлены как `409`, direct-link denial как `403`.
- Канонические Web routes `/dashboard-editor/{dashboardId}` и `/mimic-editor/{mimicId}` принадлежат разным entity/workflow; Dashboard editor редактирует windows/widgets/Current-Alarm-History bindings и exact dependencies, включая обязательный History source.
- SVG Mimic Editor редактирует отдельные SVG content/bindings/dependencies, показывает только Server-sanitized preview и публикует только exact сохранённую и validated revision.
- Общий editor workflow state явно различает loaded/saved, unsaved, validated и conflict; любое изменение снимает готовность publish, а `409` требует reload/reconcile вместо скрытого overwrite.
- Dashboard и Mimic direct routes проверяют отдельные read permissions до выдачи draft content; permission одной editor entity не открывает другую.
- Общая build policy: nullable, analyzers, code style, warnings as errors и deterministic build.
- Unit/integration test entry points и Windows x64 CI.
- Implementation sequence и sprint catalog.

## Provisional

- exact .NET 10 SDK feature-band/patch beyond the repository baseline;
- module persistence schemas beyond implemented Platform/Personal Workspace/Facility/Equipment/Configuration/Simulator activation/Core runtime/History/Alarm/Event/Dashboard owners;
- production process topology;
- protocol isolation mechanism;
- IAM/IdP mechanism;
- numeric capacity/retention/SLO limits;
- command enablement;
- native/service extraction.

## Известные риски

- Initial in-process Simulator не доказывает безопасность co-hosting real protocol I/O.
- Linux x64 build/test/load evidence отсутствует по указанию пользователя; полный acceptance `IG-01` и platform parity `S05/S14` не заявляются.
- Локальная Visual Studio 2026 использует preview SDK `10.0.400-preview.0.26322.102`; Windows validation выполнена на нём.
- Initial History query/retention profile выполнен; production numeric capacity, retention и SLO limits остаются не квалифицированы.
- Full Incident, Maintenance и IAM scope ограничен maturity границами Web/API requirements.
- Production commands требуют нескольких независимых gates; `DG-05` сам по себе ничего не включает.

## Правило обновления

После каждого sprint обновить: завершённый ID/commit, следующий разрешённый sprint, gate status, новые Stable/Provisional решения и blockers. Историю подробных изменений хранить в Git/ADR, а не раздувать этот файл.
