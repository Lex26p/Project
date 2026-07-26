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
| C01 | Ready | Первая реализационная задача |
| C02 | Planned | После C01 |
| C03 | Planned | После C02 |
| C04 | Planned | После C03 |
| C05 | Planned | После C03 |
| C06 | Planned | После C05 |
| C07 | Planned | Gate R2 |
| C08 | Planned | После production Web composition |
| C09 | Planned | После runtime pipeline и Web shell |
| C10 | Planned | После current feed и Web shell |
| C11 | Planned | После Simulator E2E |
| C12 | Planned | После configuration reconciliation |
| C13 | Planned | После configuration reconciliation |
| C14 | Planned | После обоих protocol adapters |
| C15 | Planned | Требует доступа пользователя к устройствам |
| C16 | Planned | После Event Dispatcher |
| C17 | Planned | После Incident/My Work |
| C18 | Planned | После Web shell/Event core |
| C19 | Planned | После Notifications/Web shell |
| C20 | Planned | Только Simulator commands |
| C21 | Planned | После core product и protocol acceptance |
| C22 | Planned | Финальная Windows-приёмка |
| C23 | Planned | Условный анализ после C22 |

## Правило обновления

После выполнения задания исполнитель меняет только его строку на `Complete` и переводит непосредственные зависимости, для которых выполнены все prerequisites, в `Ready`. Если критерии не выполнены, статус `Complete` не устанавливается.
