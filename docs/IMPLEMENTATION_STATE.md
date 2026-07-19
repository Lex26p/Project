# Dispatcher — состояние реализации

**Обновлено:** 19 июля 2026 года  
**Статус программы:** `S09` реализован и проверен на Windows x64; остановлено перед `S10`

**Последний завершённый пакет:** `S09` — scoped configuration revision and release lifecycle (`1e777d2` + working tree, commit не создан)

## Следующая работа

`S10` — multi-row Equipment staging editor contract из `./DISPATCHER_SPRINT_CATALOG.md`. В текущей работе не начат.

Windows x64 evidence для `S09`: locked restore и format verification green; Release build — 0 warnings/0 errors; 39 unit + 28 integration tests green. Integration tests использовали отдельный временный PostgreSQL cluster без Docker.

Linux x64 build/test не выполнялись по прямому указанию пользователя; соответствующее evidence `IG-01` остаётся открытым.

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
| `IG-06` Protocol isolation | Open | Закрыть `S23` |
| `IG-07` Command security | Open | Закрыть до `S37` |
| `IG-08` Physical write | Not authorized / deny | Только решение пользователя и scoped qualification gates перед `S39`; full `DG-08` и final production enablement не раньше `S43` |
| `IG-09` Extraction | Not justified / remain in current deployable | Открывать только по evidence и ADR |
| `IG-10` Production operations | Open | Bounded baseline до operations code в `S41`; final AR-10 consolidation по evidence до `S42` |
| `IG-11` Product maturity | Not authorized for provisional scope | Только отдельное решение пользователя |
| `IG-12` Terminal enrollment | Open | Закрыть до production identity в `S33` |
| `IG-13` Notification provider | Open | Закрыть до provider code в `S28`; default SMTP |

## Stable

- Архитектурные инварианты `AR-01` — `AR-06` в актуализированной редакции.
- `ADR-001` C#-first.
- `ADR-002`: .NET 10/`net10.0`, `.slnx`, central package versions и locked restore.
- `ADR-003`: canonical typed identity, revision/version, result/error, value/unit/quality/freshness, distinct timestamps, clocks и owner-scoped positions/cursors.
- `ADR-004`: PostgreSQL/Npgsql, module-owned schemas/roles/migration histories и transactional owner obligation.
- `ADR-005`: user/device/workload sessions, exact effective permissions, operation admission/idempotency/audit и environment-gated test identity.
- `ADR-006`: ASP.NET Core HTTP + SignalR, per-subscription opaque Web cursor, authorized point filtering и mandatory resnapshot после gap/reconnect/permission-set change.
- Platform nucleus использует structured activities/metrics, database readiness и lease-based durable jobs без workflow engine.
- Simulator генерирует deterministic observations по seed/config и не получает mutable Core current.
- Core владеет scope admission/current, scoped snapshot/delta и canonical golden trace; restart сохраняет configured identities.
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
- Integration DB lifecycle использует отдельный временный PostgreSQL cluster без Docker и не изменяет рабочую БД.
- Общая build policy: nullable, analyzers, code style, warnings as errors и deterministic build.
- Unit/integration test entry points и Windows x64 CI.
- Implementation sequence и sprint catalog.

## Provisional

- exact .NET 10 SDK feature-band/patch beyond the repository baseline;
- module persistence schemas beyond implemented Platform/Personal Workspace/Facility/Equipment/Configuration owners;
- production process topology;
- protocol isolation mechanism;
- IAM/IdP mechanism;
- numeric capacity/retention/SLO limits;
- command enablement;
- native/service extraction.

## Известные риски

- Initial in-process Simulator не доказывает безопасность co-hosting real protocol I/O.
- Linux x64 build/test evidence отсутствует по указанию пользователя; полный исходный acceptance `IG-01` не заявляется.
- Linux parity canonical Simulator trace для `S05` не проверялась по указанию пользователя.
- Локальная Visual Studio 2026 использует preview SDK `10.0.400-preview.0.26322.102`; Windows validation выполнена на нём.
- Data/history capacity profile пока не измерен.
- Full Incident, Maintenance и IAM scope ограничен maturity границами Web/API requirements.
- Production commands требуют нескольких независимых gates; `DG-05` сам по себе ничего не включает.

## Правило обновления

После каждого sprint обновить: завершённый ID/commit, следующий разрешённый sprint, gate status, новые Stable/Provisional решения и blockers. Историю подробных изменений хранить в Git/ADR, а не раздувать этот файл.
