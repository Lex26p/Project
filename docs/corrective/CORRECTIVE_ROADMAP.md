# Dispatcher — дорожная карта корректирующей программы

## 1. Модель выполнения

Каждое задание закрывает один ограниченный результат. Номер задаёт рекомендуемый порядок. Следующая задача может использовать только принятые контракты предыдущих задач.

| ID | Результат | Зависит от |
|---|---|---|
| C01 | Production DatabaseMigrator и воспроизводимый migration startup | S38 |
| C02 | RuntimeHost действительно опрашивает Simulator через bounded production loop | C01 |
| C03 | Durable processing delivery и PostgreSQL published current contract | C02 |
| C04 | Автоматическая History → Alarm → Event delivery | C03 |
| C05 | Server читает published current из PostgreSQL | C03 |
| C06 | Same-origin Web, production session и SignalR Long Polling | C05 |
| C07 | Межпроцессный Simulator E2E и recovery corpus | C04, C06 |
| C08 | Полноценная Web shell и общие UI-состояния | C06 |
| C09 | Диспетчер событий, alarm actions и History workspace | C04, C08 |
| C10 | Dashboard, mimic и kiosk runtime/editor completion | C05, C08 |
| C11 | Workload configuration reconciliation и безопасная activation | C07 |
| C12 | Modbus TCP read-only production source | C11 |
| C13 | SNMP v2c read-only production source | C11 |
| C14 | Engineering staging, commissioning и durable diagnostics | C12, C13 |
| C14A | Decimal semantics измеряемых значений | C14 |
| C15 | Лабораторная приёмка реальных Modbus/SNMP устройств | C14, C14A |
| C16 | Incident + My Work Core API и Web | C09 |
| C17 | Maintenance Core API и Web | C16 |
| C18 | Notification inbox, policy и delivery visibility | C08, C09 |
| C19 | Administration, operational health и profile completion | C08, C18 |
| C20 | Simulator-only command UX и доказанный physical hard deny | C07, C10 |
| C21 | Windows services, health, logs, metrics и runbook | C15, C18, C19, C20 |
| C22 | Backup/restore, load/soak и финальный acceptance corpus | C21 |
| C23 | Измерительный C++ decision gate | C22 |

## 2. Контрольные точки

### Gate R1 — миграционная основа

После C01:

- чистая PostgreSQL 17 получает все schemas одним запуском;
- повторный запуск ничего не меняет;
- RuntimeHost и Server не применяют миграции самостоятельно.

### Gate R2 — производственный Simulator

После C07:

- отдельные процессы проходят Simulator → RuntimeHost → PostgreSQL → Server → Web;
- history, alarm и event создаются автоматически;
- рестарт и кратковременный отказ PostgreSQL не теряют pending delivery;
- медленный Web восстанавливается snapshot/resync.

### Gate R3 — реальные источники

После C15:

- Modbus FC03/FC04 и SNMP GET работают на предоставленных устройствах;
- timeout, disconnect и recovery подтверждены;
- секреты не раскрываются;
- ни один write path не зарегистрирован.

### Gate R4 — core product

После C20:

- основные пользовательские Web-сценарии соответствуют спецификации;
- Simulator command lifecycle работает;
- physical command остаётся hard deny.

### Gate R5 — Windows production candidate

После C22:

- установка, запуск, stop/restart, backup/restore и диагностика воспроизводимы;
- fault/load/soak corpus пройден;
- остающиеся ограничения перечислены явно.

## 3. Scope discipline

- C01–C07 имеют высший приоритет: без них UI не должен считаться подключённым к реальной системе.
- C08–C20 реализуют только проработанные Core-capabilities. Provisional placeholders не детализируются.
- C12–C15 не разрешают физические команды.
- C21–C22 выполняются только на Windows 11 с PostgreSQL 17.
- C23 не является заданием на C++-разработку.

## 4. Изменение дорожной карты

Младшая модель может уточнять локальную реализацию внутри задания. Изменение порядка owners, process topology, transport, security boundary или физического protocol scope является blocking architecture decision и не выполняется молча.
