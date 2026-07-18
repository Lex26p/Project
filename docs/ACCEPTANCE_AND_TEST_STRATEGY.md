# Dispatcher — стратегия приёмки и тестирования

**Статус:** нормативная  
**Дата:** 18 июля 2026 года  
**Применяется совместно с:** `./DISPATCHER_SPRINT_CATALOG.md`

## 1. Принцип

Тестирование является частью каждого спринта. Crash, recovery, security, overload и Linux verification начинаются в первом затрагиваемом модуле и не откладываются до `E16`. Финальная приёмка повторяет накопленный corpus на production-like topology.

## 2. Классы тестов

| Класс | Обязательное назначение | Первый обязательный этап |
|---|---|---|
| Unit/model | Owner transitions, validation, value/time, permission composition | `E01` |
| Property | Monotonic versions, idempotency, stale no-op, bounded state, no mixed revision | `E01/E02` |
| Golden | Deterministic Simulator, RuntimeCut, timers, activation, Alarm | `E02`, расширяется `E05/E07` |
| Contract | Web↔Server, Core planes, owner/consumer acceptance, compatibility | `E02` |
| Integration | DB, migrations, jobs, providers, module consumers | `E01` и каждый модуль |
| End-to-end | Реальные пользовательские маршруты через Web→Server | `E02` и каждый accepted module |
| Fault injection | Crash, disconnect, gap, replay, checkpoint, timeout, partial failure | `E02`, затем постоянно |
| Security | AuthN/AuthZ, BOLA/IDOR, revoke, secret/file/parser, terminal/command races | `E01`, затем постоянно |
| Fuzz/robustness | CSV/SVG/protocol/filter/payload bounds | Первый parser/ingress соответствующего типа |
| Load/soak | Fan-out, reconnect, Alarm flood, History, jobs, protocol churn, resource bounds | `E05`, затем по workload |
| Platform | Semantic parity Windows/Linux; Linux production evidence | `E01` и каждый acceptance gate |
| Recovery/operations | Backup/restore, restart, upgrade/rollback, degraded mode | С `E01/E04`; полный gate `E16` |

## 3. Platform matrix

| Platform | Статус |
|---|---|
| Windows x64 | Обязательна для development build/unit/integration parity |
| Linux x64 | Обязательна для CI и production acceptance authority |
| Linux ARM64 | Provisional; включается после явного target/evidence decision |
| Browser matrix | Определяется Web gate; как минимум один production browser и один compatibility browser |

Windows-only pass не является production evidence.

## 4. Накопительные stage gates

| Этап | Обязательные проверки до перехода |
|---|---|
| `E01` | Clean build, migrations, transaction rollback, session/revoke, audit/idempotency, Linux CI |
| `E02` | Deterministic trace, scoped authorization, snapshot/delta/gap/resync, Server/Core restart |
| `E02A` | Canonical routes, shell/navigation, direct links, viewer filtering, session expiry, profile/preferences persistence |
| `E03` | Identity/relationship invariants, scope isolation, registry metadata leakage, restart |
| `E04` | Revision races, stale validation, whole-scope activation crash points, hostile CSV, secret non-disclosure |
| `E05` | Generation fencing, timer/order properties, evidence exhaustion, bounded queues, burst/soak |
| `E06` | Independent positions, duplicate/late ingest, gaps, retention, query load, live-tail handoff |
| `E07` | Single evaluator, occurrence facet races, immutable Event, Alarm flood, restart/no duplicate |
| `E08` | Binding authorization, slow consumer isolation, revision switch, malicious SVG, high cardinality |
| `E09` | Hostile protocol input, disconnect/reconnect/process crash, read-only proof, secret handling, Linux OT boundary |
| `E10` | Provider outage/retry/duplicate, mandatory coverage, inbox independence, substitution races |
| `E11/E12` | Cross-owner independence, lifecycle concurrency, scheduler idempotency, projection rebuild |
| `E13/E14` | Enrollment replay/revoke, Wallboard deny, permission invalidation, audit/health overload |
| `E15` | Lease/step-up/revoke/stale revision, timeout unknown, reconciliation, no physical effect |
| `E15B` | Per-command safety, uncertain/partial outcome, disable/rollback, protocol/device qualification |
| `E16` | Install/restore/upgrade, full security/fault/load/soak matrix и end-to-end release acceptance |

## 5. Трассировка к Web/Core/Server criteria

### Web

Все применимые non-provisional критерии раздела 36 и маршруты раздела 32 `../../WEB_INTERFACE_SPECIFICATION.md` обязательны.

| Web scope | Первое прохождение | Обязательное повторение |
|---|---|---|
| Global shell/navigation/context/common states, login/errors, Home/person/profile/search | `S06A` | Каждый затронутый Web sprint; полный проход `S43` |
| Locations/equipment | `S08` | `S26`, `S43` |
| History/trends | `S16` | `S21`, `S43` |
| Events/Alarms/Incidents/My Work | `S18–S19`, `S29` | `S43` |
| Dashboards/Mimics/runtime и оба редактора | `S20–S22A` | `S43` |
| Notifications | `S27–S28` | `S43` |
| Maintenance | `S30–S32` | `S43` |
| Kiosk/Wallboard | `S33–S34` | `S43` |
| Administration/health/audit | `S35–S36` | `S43` |
| Safe control UI | `S37–S40` по enabled scope | `S43` |

Provisional Web sections не отмечаются `Passed`; они остаются вне release scope до `IG-11`.

### Core

`../../CORE_ARCHITECTURE_SPECIFICATION.md`, разделы 52–53, распределяются так:

| Группа | Спринты первого прохождения | Обязательное повторение |
|---|---|---|
| Value/type/time/generation и deterministic trace | `S02`, `S05`, `S12` | `S14`, `S42` |
| RuntimeCut, timers, liveness, mass expiry | `S12` | `S14`, `S19`, `S42` |
| Activation/migration/AP-SRC | `S11`, `S13` | `S26`, `S42` |
| Snapshot/protected replay/gaps | `S06`, `S13` | `S21`, `S42` |
| Alarm state/restart | `S17`, `S19` | `S42` |
| Production physical-command-ready remains false | С `S05` до `S38` | Непрерывно до `S42`; `S39–S40` проверяют только isolated qualification capability, final status принимается в `S43` |

### Server

Все `AR06-TST-001` — `AR06-TST-047` из `../../SERVER_ARCHITECTURE_SPECIFICATION.md` остаются обязательными. Первое прохождение:

| Criteria | Первый основной спринт/группа | Финальное повторение |
|---|---|---|
| `001–016`, `042`, `045` | `S04`, `S06`, `S33–S38` по применимости | `S42` |
| `017–030`, `043`, `046–047` | `S06`, `S11`, `S13`, `S21` | `S42` |
| `031–033` | `S08`, `S16`, `S18`, `S29` | `S42` |
| `034–037` | `S09–S11`, `S22`, `S24–S26` | `S42` |
| `038–041` | `S03–S04`, `S13`, `S28`, `S35` | `S42` |
| `044` | `S14`, затем каждый high-load module | `S42` |

Если критерий ещё не применим, он отмечается `Not applicable until Sxx`, а не `Passed`.

## 6. Evidence artifacts

Для acceptance сохраняются в CI/release evidence:

- build/test report с commit и environment fingerprint;
- migration/restore report;
- canonical golden traces и diff;
- contract compatibility report;
- security/fuzz/fault report;
- workload profile и raw load/soak metrics;
- Linux package/install/upgrade/rollback result;
- список release-blocking failures и disposition.

Runtime logs не заменяют автоматическое assertion. Experimental performance result не становится нормативным limit без ADR.

## 7. Release-blocking правила

Всегда release-blocking:

- permission/metadata/secret leak;
- dual writer или cross-owner unauthorized mutation;
- mixed published/runtime revision;
- silent loss accepted protected fact;
- duplicate authoritative effect;
- silent gap либо realtime-as-History;
- false freshness/command result;
- unbounded resource growth в accepted workload profile;
- dev/test identity либо default secret в production profile;
- physical write без всех gates и explicit enablement;
- невозможность clean install, restore или supported rollback на Linux.

Обычный UI cosmetic defect блокирует release только если мешает безопасно выполнить accepted workflow, различить состояние/качество либо соблюсти accessibility requirement.

## 8. Test data и безопасность

- Simulator fixtures детерминированы и versioned.
- Test credentials явно environment-gated и не совпадают с production defaults.
- Secrets не входят в snapshots, golden files, logs и artifacts.
- Hostile CSV/SVG/protocol payloads хранятся как безопасные test fixtures.
- Clock, network, provider и process failures должны быть управляемо inject-able, без зависимости от случайного timing.

## 9. Final acceptance

`S43` может завершиться только при green всех release-blocking criteria заявленного scope, закрытых applicable gates, воспроизводимом Linux artifact и актуальном `./IMPLEMENTATION_STATE.md`. Provisional capabilities перечисляются как ограничения, но не маскируются фиктивными заглушками.
