# Dispatcher — отчёт о подготовке программы реализации

**Статус:** завершено, готово к `S01`  
**Дата:** 18 июля 2026 года

## 1. Результат

Сформирован единый implementation-комплект для последовательной разработки Dispatcher другим AI-агентом. Комплект не пересоздаёт продукт и Web UX, не содержит исходного кода и переводит ранее согласованные требования в C#-first roadmap, execution-спринты, gates, acceptance policy и короткий рабочий протокол пользователя.

## 2. Использованные источники

- `../../outputs/product_concept_dispatching_platform.md`;
- `../../WEB_INTERFACE_SPECIFICATION.md`;
- `../../WEB_BACKEND_API_REQUIREMENTS.md`;
- `../../SYSTEM_ARCHITECTURE_ROADMAP.md`;
- `../../BACKEND_ARCHITECTURE_CONCEPT.md`;
- `../../CORE_ARCHITECTURE_SPECIFICATION.md`;
- `../../SERVER_ARCHITECTURE_SPECIFICATION.md`.

Старые release-specific комплекты в `../specification/`, `../planning/`, ранние baseline/review outputs и HTML-прототип не использовались как implementation authority. Они не удалены и могут применяться только как явно запрошенный historical evidence.

## 3. Созданные документы

| Документ | Назначение |
|---|---|
| `./ADR-001_CSHARP_FIRST_RUNTIME.md` | C#/.NET first и evidence-gated C++/service extraction |
| `./DISPATCHER_MASTER_IMPLEMENTATION_SPECIFICATION.md` | Source priority, invariants, module/deployable boundaries и Definition of Done |
| `./DISPATCHER_IMPLEMENTATION_ROADMAP.md` | Полная dependency-driven программа, stages и decision gates |
| `./DISPATCHER_SPRINT_CATALOG.md` | Исполняемые задания `S01–S43` с acceptance и non-goals |
| `./ACCEPTANCE_AND_TEST_STRATEGY.md` | Нарастающая functional, recovery, security, load и platform verification |
| `./AI_IMPLEMENTATION_RULES.md` | Границы и рабочий контракт следующего AI-агента |
| `./USER_IMPLEMENTATION_GUIDE.md` | Краткий цикл commit → sprint → test → commit |
| `./IMPLEMENTATION_STATE.md` | Единственная компактная точка продолжения программы |
| `./IMPLEMENTATION_PREPARATION_REPORT.md` | Настоящий итоговый отчёт |

## 4. Актуализированные архитектурные документы

- `../../SYSTEM_ARCHITECTURE_ROADMAP.md` — C#-first baseline, ранняя bounded topology и final `AR-10` consolidation;
- `../../BACKEND_ARCHITECTURE_CONCEPT.md` — язык реализации отделён от authority/process boundaries, уточнён command gate;
- `../../CORE_ARCHITECTURE_SPECIFICATION.md` — C#/.NET first, production hard-deny отделён от isolated qualification profile;
- `../../SERVER_ARCHITECTURE_SPECIFICATION.md` — Server/Web границы согласованы с C#-first implementation program.

Product scope и Web specification не переписывались.

## 5. Объём программы

| Показатель | Значение |
|---|---:|
| Технические этапы | 19 |
| Implementation-спринты | 45 |
| Execution packages с завершённым `S00` | 46 |
| Implementation gates | 14 |
| Обязательные protocol profiles | Modbus TCP read-only, SNMP read-only |
| Основные production platforms | Linux x64; Windows x64 development/test |
| Подготовительный platform target | Linux ARM64 compile/readiness по evidence |

Полный mandatory join охватывает foundation, walking skeleton, personal workspace, Facility/Equipment, configuration, runtime, History, Event Dispatcher/Alarm, dashboards/mimics/editors, protocols, notifications, My Work/Incident nucleus, maintenance, kiosk/wallboard, administration и production operations. Physical commands выделены в условную qualification-ветвь; production enablement не считается обязательным по умолчанию.

## 6. Закрытые противоречия

1. C++-first заменён на C#/.NET-first без изменения Core authority; native extraction разрешена только по измеренному evidence и `IG-09`.
2. «Все модули понемногу» заменено на минимальный walking skeleton и последующее завершение модулей по dependency order.
3. Один язык не трактуется как одна authority boundary: Web обращается только к Server, Server не выполняет protocol I/O, Core сохраняет current/local Alarm/runtime authority.
4. Simulator может быть co-hosted для раннего skeleton; real protocol baseline использует отдельный Core/runtime host. Третий protocol worker process не создаётся без extraction evidence.
5. `DG-07` не закрывается до protocol code: `S23` фиксирует evidence plan, `S26` закрывает его отдельно для Modbus TCP и SNMP read-only.
6. Full `DG-08` не является входом `E16`: operations evidence создаётся в `S41–S43`, gate закрывается в `S43`.
7. Qualification physical writes `S39–S40` и production capability разведены. Qualification требует отдельной авторизации и exact target/environment evidence; production `physical-command-ready=false` сохраняется до отдельного решения `S43`.
8. Home остаётся независимым personal workspace, а не Dashboard entity; Locations, Dashboard Editor и SVG Mimic Editor получили явный execution scope.

## 7. Проверки комплекта

- все 45 sprint IDs определены, `S00` явно отмечен завершённым;
- все 14 `IG-*` имеют определение и статус;
- ссылки на существующие Markdown-источники используют относительные пути и разрешаются;
- каждый спринт имеет вход, последовательный scope, acceptance и explicit non-goals;
- crash/recovery/security/overload проверки распределены по затрагиваемым этапам, а не отложены до финала;
- C++ projects, application code, пустые future modules и новые process boundaries не созданы;
- повторная проверка архитектурных и implementation-critical расхождений не выявила оставшихся `P0/P1`.

## 8. Открытые gates и риски

- `IG-01`: exact .NET SDK/toolchain/CI versions фиксируются в `S01`;
- `IG-03` и `IG-05`: persistence и Web/realtime transport выбираются до первой соответствующей реализации;
- `IG-04P`: production AuthN закрывается до `S35`; safe default — локальные защищённые accounts, внешний IdP provisional;
- `IG-06/IG-10`: process/security topology сначала bounded, затем консолидируется по protocol и operations evidence;
- numeric capacity, retention и SLO limits нельзя объявлять нормативными до workload evidence;
- `IG-08` не авторизован: qualification и production physical writes остаются deny;
- расширенный Incident, Maintenance и enterprise IAM scope сохраняет указанную в Web/API источниках maturity;
- текущий каталог не инициализирован как Git repository, поэтому до браузерного commit-based цикла нужен repository baseline.

## 9. Следующее действие

Начать только `S01` по `./DISPATCHER_SPRINT_CATALOG.md`, предварительно зафиксировав repository baseline. Следующий AI-агент получает актуальный commit, `S01`, `./AI_IMPLEMENTATION_RULES.md` и читает только прямо указанные для шага источники.
