# ADR-001 — C#-first реализация Dispatcher Core/runtime

**Статус:** Accepted  
**Дата:** 18 июля 2026 года  
**Область:** язык первой реализации и допустимые границы последующего native extraction

## Контекст

`../../CORE_ARCHITECTURE_SPECIFICATION.md` определяет Core как логическую runtime/authority boundary. Ранее первая реализация была привязана к C++. Для разработки через AI-агента такая привязка повышает стоимость одновременного сопровождения двух toolchain, межъязыкового hosting и диагностики до получения данных о реальной нагрузке.

Семантика Core уже определена независимо от языка: runtime scope/epoch, deterministic execution, current, local Alarm authority, activation, protected evidence, recovery и physical-command hard deny.

## Решение

1. Первая реализация Dispatcher Core/runtime выполняется на C#/.NET.
2. Dispatcher Server остаётся C#/ASP.NET Core application/security boundary; Dispatcher Web — C#/Blazor WebAssembly.
3. Core не объединяется с Server на уровне authority. Общий язык и возможный initial co-hosting не разрешают Server выполнять protocol I/O либо присваивать current, local Alarm, active runtime set или CommandExecution.
4. Simulator/no-OT walking skeleton может использовать in-process Core/runtime modules.
5. Process topology для production Modbus TCP/SNMP утверждается до protocol I/O. Без доказанной безопасности co-hosting используется отдельная runtime process/workload identity.
6. C++-проекты, C ABI, P/Invoke, native plugins и IPC не создаются заранее.
7. Native/C++ extraction допускается только после измеренной performance, fault-isolation или security need, решения `DG-06`, стабильного semantic contract, parity/golden tests и migration/rollback plan.
8. Extraction заменяет реализацию, но не меняет owner, authority, positions, failure semantics и Web→Server boundary.

## Неизменяемые инварианты

- Web/Kiosk/Wallboard обращаются только к Server.
- Server не выполняет southbound protocol I/O.
- Core/runtime остаётся единственной logical southbound authority boundary.
- Local Alarm authority принадлежит Core.
- Realtime не является History; positions/cursors разных owners не объединяются.
- Publication не равна runtime activation.
- Production physical writes остаются hard-deny до полного command/security/protocol/operations gate; isolated qualification profile `S39–S40` является отдельной user-authorized capability и не включает production readiness.
- Bounded context, project, process и microservice не являются эквивалентами.

## Контролируемое замещение прежних формулировок

| Источник | Замещается | Сохраняется |
|---|---|---|
| `../../SYSTEM_ARCHITECTURE_ROADMAP.md` | C++-first toolchain и первая C++→C# вертикаль | Порядок gates, Core↔Server boundary, Linux acceptance |
| `../../BACKEND_ARCHITECTURE_CONCEPT.md` | Native-first hosting alternatives | Authority, topology alternatives, failure/continuity semantics |
| `../../CORE_ARCHITECTURE_SPECIFICATION.md` | C++/CMake/compiler assumptions | Все `AR05-DEC-*`, runtime и test semantics |
| `../../SERVER_ARCHITECTURE_SPECIFICATION.md` | Native-layout wording | Все `AR06-DEC-*`, Server authority и realtime semantics |

Исторические записи AR-01 — AR-06 не переписываются. Настоящий ADR имеет приоритет только в вопросе языка первой реализации и раннего hosting.

## Последствия

Положительные:

- единый initial toolchain и test stack;
- более короткий путь к walking skeleton;
- отсутствие преждевременного межъязыкового контракта;
- возможность измерить реальные bottlenecks до extraction.

Обязательства:

- runtime kernel не зависит от ASP.NET Core, Blazor, EF Core либо UI DTO;
- deterministic golden suite является переносимым oracle;
- protocol adapter не получает runtime authority;
- production OT process/security boundary закрывается отдельным решением;
- общий process не используется как доказательство правильности production topology.

## Критерий пересмотра

ADR пересматривается только при наличии воспроизводимого профиля нагрузки или security/fault-isolation требования, которое C#/.NET implementation не удовлетворяет после обычной оптимизации. Предпочтение языка само по себе не является evidence.
