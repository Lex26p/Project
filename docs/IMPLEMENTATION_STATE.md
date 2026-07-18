# Dispatcher — состояние реализации

**Обновлено:** 19 июля 2026 года  
**Статус программы:** `S01` реализован и проверен на Windows x64; остановлено перед `S02`  
**Последний завершённый пакет:** `S01` — .NET 10 solution/build/test/Windows CI foundation (working tree, commit не создан)

## Следующая работа

Следующий sprint в текущей работе не разрешён. `S02` не начат.

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
| `IG-02` Semantic contracts | Open | Закрыть `S02` |
| `IG-03` Data/persistence | Open | Закрыть до persistence implementation в `S03` |
| `IG-04` Session/security nucleus | Open | Закрыть `S04` |
| `IG-04P` Production AuthN | Open | Закрыть до production login в `S35` |
| `IG-05` Web/realtime transport | Open | Закрыть до `S06` |
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
- Общая build policy: nullable, analyzers, code style, warnings as errors и deterministic build.
- Unit/integration test entry points и Windows x64 CI.
- Implementation sequence и sprint catalog.

## Provisional

- exact .NET 10 SDK feature-band/patch beyond the repository baseline;
- persistence/transport implementation;
- production process topology;
- protocol isolation mechanism;
- IAM/IdP mechanism;
- numeric capacity/retention/SLO limits;
- command enablement;
- native/service extraction.

## Известные риски

- Initial in-process Simulator не доказывает безопасность co-hosting real protocol I/O.
- Linux x64 build/test evidence отсутствует по указанию пользователя; полный исходный acceptance `IG-01` не заявляется.
- Локальная Visual Studio 2026 использует preview SDK `10.0.400-preview.0.26322.102`; Windows validation выполнена на нём.
- Data/history capacity profile пока не измерен.
- Full Incident, Maintenance и IAM scope ограничен maturity границами Web/API requirements.
- Production commands требуют нескольких независимых gates; `DG-05` сам по себе ничего не включает.

## Правило обновления

После каждого sprint обновить: завершённый ID/commit, следующий разрешённый sprint, gate status, новые Stable/Provisional решения и blockers. Историю подробных изменений хранить в Git/ADR, а не раздувать этот файл.
