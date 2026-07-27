# C07 — Межпроцессный Simulator E2E и recovery corpus: итоговый отчёт

## 1. Результат

Реализован изолированный Windows process test layer, который проверяет совместную
работу production-компонентов C01–C06 через реальные границы процессов и
PostgreSQL 17.

E2E harness:

- создаёт отдельную временную PostgreSQL database;
- запускает `Dispatcher.DatabaseMigrator`, `Dispatcher.RuntimeHost` и
  `Dispatcher.Server` как отдельные процессы;
- публикует Server во временный каталог, чтобы WebAssembly assets проверялись в
  production layout;
- готовит уникальные identity, configuration и Simulator fixtures через
  production stores;
- использует production login, session authorization, HTTP API и SignalR Long
  Polling;
- гарантированно завершает процессы и удаляет временные ресурсы.

Общая DI container между процессами не использовалась.

## 2. Process topology

```text
E2E test process
├── temporary PostgreSQL 17 database
├── Dispatcher.DatabaseMigrator process
├── dotnet publish Dispatcher.Server process
├── Dispatcher.RuntimeHost process
└── Dispatcher.Server process
    ├── same-origin Web index
    ├── production session API
    ├── runtime snapshot API
    └── runtime SignalR hub
```

Для контролируемого connection fault RuntimeHost подключается к PostgreSQL через
временный loopback TCP proxy. Proxy закрывает listener и активные соединения, а
затем возобновляет работу на том же порту.

## 3. Проверенные сценарии

- DatabaseMigrator применяет production migration plans отдельным процессом.
- Production fixtures создаются со случайными паролями и уникальными
  identifiers.
- Web index доступен с опубликованного Server.
- Production login и session authorization не заменяются тестовым обходом.
- До завершения History → Alarm → Event pipeline опубликованный current snapshot
  недоступен.
- RuntimeHost аварийно завершается между History и Alarm stages.
- Новый RuntimeHost восстанавливает незавершённую delivery без duplicate history,
  alarm и event records.
- HTTP snapshot становится доступен только после завершения downstream pipeline.
- SignalR возвращает delta.
- Отставший consumer получает gap после выхода cursor за retention window и
  выполняет resnapshot.
- Server штатно останавливается, запускается повторно и принимает ранее созданную
  актуальную production session.
- При контролируемом разрыве PostgreSQL-соединения RuntimeHost остаётся жив,
  переходит в transient retry и после восстановления соединения продолжает
  публикацию.
- RuntimeHost и Server завершаются через перенаправленный stdin cancellation
  equivalent с проверкой успешного exit code.

## 4. Детерминизм и диагностика

- Для startup, readiness, condition и shutdown используются bounded waits.
- Произвольные `sleep` не используются.
- Каждый процесс проверяется на преждевременное завершение.
- stdout/stderr захватываются в ограниченный буфер: не более 500 строк и 2000
  символов на строку.
- Connection strings, passwords и session credentials редактируются перед
  диагностическим выводом.
- Fixture passwords создаются во время теста и не сохраняются в repository.
- Все fault-сценарии ограничены timeout; для test runner установлен hang timeout
  4 минуты.

## 5. Cleanup

После успешного сценария harness проверяет:

- отсутствие каждого запущенного process PID;
- отсутствие временной database в PostgreSQL;
- удаление временного Server publish directory;
- закрытие TCP fault proxy.

Те же cleanup-действия выполняются в `finally` при ошибке. Удаление database имеет
ограниченное число повторов и принудительно завершает только подключения к
конкретной временной database.

## 6. Изменённые файлы

### Process lifecycle

- `src/Dispatcher.RuntimeHost/Program.cs`
- `src/Dispatcher.Server/Program.cs`

### Process E2E layer

- `tests/Dispatcher.IntegrationTests/Process/ManagedDispatcherProcess.cs`
- `tests/Dispatcher.IntegrationTests/Process/TcpFaultProxy.cs`
- `tests/Dispatcher.IntegrationTests/Process/SimulatorProcessE2ETests.cs`

### Corrective documentation

- `docs/corrective/STATUS.md`
- `docs/corrective/C07_SIMULATOR_PROCESS_E2E_REPORT.md`

## 7. Проверки

Process E2E стабильно прошёл два последовательных запуска:

- первый успешный запуск: 41 секунда;
- повторный успешный запуск: 30 секунд.

Выполнены обязательные команды:

```powershell
dotnet restore Dispatcher.slnx --locked-mode
dotnet build Dispatcher.slnx --configuration Release --no-restore
dotnet test tests/Dispatcher.UnitTests/Dispatcher.UnitTests.csproj --configuration Release --no-build --no-restore
dotnet test tests/Dispatcher.IntegrationTests/Dispatcher.IntegrationTests.csproj --configuration Release --no-build --no-restore
```

Результат:

- locked restore успешен;
- Release build: 0 предупреждений, 0 ошибок;
- Unit: 109 успешно, 0 сбоев, 0 пропущено;
- Integration: 120 успешно, 0 сбоев, 0 пропущено, 5 минут 56 секунд;
- всего: 229 тестов успешно.

Использовались только Windows x64 и временные PostgreSQL 17 resources. Docker,
Linux, пользовательская database и Git не использовались. Blocking decisions и
оставшиеся ограничения в объёме C07 отсутствуют.
