# Dispatcher — статус корректирующей программы

## Статусы

- `Ready` — задание полностью сформулировано, prerequisites выполнены.
- `Planned` — задание сформулировано, но ждёт prerequisites.
- `In progress` — выполняется.
- `Complete` — критерии задания и проверки выполнены.
- `Blocked` — требуется архитектурное или пользовательское решение.
- `Skipped` — условная задача обоснованно не требуется.
## Текущее состояние
| ID | Статус | Примечание |
|---|---|---|
| C01 | Complete | Production DatabaseMigrator реализован и принят |
| C02 | Complete | Production RuntimeHost и Simulator polling реализованы и приняты |
| C03 | Complete | Durable processing delivery и published current реализованы и приняты |
| C04 | Complete | Автоматическая History → Alarm → Event pipeline реализована и принята |
| C05 | Complete | PostgreSQL published current reader подключён к Server и принят |
| C06 | Complete | Same-origin Web, production session и runtime realtime реализованы и приняты |
| C07 | Complete | Межпроцессный Simulator E2E и recovery corpus реализованы и приняты |
| C08 | Complete | Web shell, visual system и browser baseline реализованы и приняты |
| C09 | Complete | Event Dispatcher, Alarm actions и read-only History workspace реализованы и приняты |
| C10 | Complete | Dashboard, mimic и kiosk runtime/editor реализованы и приняты |
| C11 | Complete | Workload configuration reconciliation и безопасная activation реализованы и приняты |
| C12 | Ready | Configuration reconciliation C11 выполнена |
| C13 | Ready | Configuration reconciliation C11 выполнена |
| C14 | Planned | После обоих protocol adapters |
| C15 | Planned | Требует доступа пользователя к устройствам |
| C16 | Ready | Event Dispatcher C09 выполнен |
| C17 | Planned | После Incident/My Work |
| C18 | Ready | Web shell C08 и Event core C09 выполнены |
| C19 | Planned | После Notifications/Web shell |
| C20 | Ready | Dashboard/mimic/kiosk C10 и межпроцессный Simulator E2E C07 выполнены |
| C21 | Planned | После core product и protocol acceptance |
| C22 | Planned | Финальная Windows-приёмка |
| C23 | Planned | Условный анализ после C22 |
## Правило обновления

После выполнения задания исполнитель меняет только его строку на `Complete` и переводит непосредственные зависимости, для которых выполнены все prerequisites, в `Ready`. Если критерии не выполнены, статус `Complete` не устанавливается.
