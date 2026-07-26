# C02 — RuntimeHost и production polling Simulator: итоговый отчёт

## 1. Результат

`Dispatcher.RuntimeHost` превращён из процесса, который только восстанавливал Core и ожидал остановки, в production runtime для активного Simulator manifest.

Реализованный контур:

1. RuntimeHost создаёт ограниченную production-сессию.
2. Core выполняет recovery до регистрации binding и начала polling.
3. RuntimeHost читает active Simulator manifest из PostgreSQL.
4. Для `(runtime scope, source)` атомарно выделяется новое durable `SourceSessionGeneration`.
5. Создаётся `SimulatorPollingSource`.
6. Binding активируется в `CoreRuntime` и существующем `BoundedPollScheduler`.
7. Poll result нормализуется в `RuntimeCut`.
8. Cut проходит durable `EnqueueAsync`.
9. Обязательства последовательно обрабатываются через `ProcessNextAsync`.
10. При остановке сначала прекращается polling, затем выполняется Core drain и освобождаются PostgreSQL resources.

C02 не добавляет published current для Server, History/Alarm/Event delivery, Web API, Modbus/SNMP polling или dynamic workload reconciliation. Эти области остаются в последующих corrective-задачах.

## 2. Durable source session generation

В `core_runtime` добавлена migration version 2 с таблицей:

- `core_runtime.source_session_generation`.

Счётчик хранится независимо для каждой пары:

- `RuntimeScopeId`;
- `SourceId`.

`CoreRuntimeStore.AllocateSourceSessionGenerationAsync` выполняет атомарный PostgreSQL upsert и возвращает следующее ненулевое поколение.

Подтверждённые свойства:

- первое поколение равно `1`;
- повторный запуск получает большее поколение;
- разные scope/source имеют независимые последовательности;
- параллельные allocations не возвращают дубликаты;
- после restart старый in-flight poll fence-ится новой binding/session и имеет результат `Stale`.

## 3. Конфигурация RuntimeHost

Обязательные переменные окружения:

- `DISPATCHER_RUNTIME_SCOPE_ID`;
- `DISPATCHER_RUNTIME_WORKLOAD_IDENTITY`;
- `DISPATCHER_RUNTIME_CONNECTION_STRING`;
- `DISPATCHER_RUNTIME_DATABASE_ROLE`;
- `DISPATCHER_RUNTIME_SIMULATOR_DATABASE_ROLE`;
- `DISPATCHER_RUNTIME_MAX_CURRENT_POINTS`;
- `DISPATCHER_RUNTIME_RETAINED_CURRENT_CHANGES`;
- `DISPATCHER_RUNTIME_INGRESS_CAPACITY`;
- `DISPATCHER_RUNTIME_MAX_PROTOCOL_SOURCES`;
- `DISPATCHER_RUNTIME_POLL_INTERVAL_MS`;
- `DISPATCHER_RUNTIME_POLL_TIMEOUT_MS`;
- `DISPATCHER_RUNTIME_SCHEDULER_MAX_BINDINGS`;
- `DISPATCHER_RUNTIME_SCHEDULER_MAX_IN_FLIGHT`;
- `DISPATCHER_RUNTIME_RECONCILIATION_INITIAL_BACKOFF_MS`;
- `DISPATCHER_RUNTIME_RECONCILIATION_MAX_BACKOFF_MS`.

Проверяются следующие relationships:

- все числовые limits и интервалы положительны;
- scheduler max in-flight не превышает max bindings;
- reconciliation initial backoff не превышает maximum backoff.

Connection string и secret values не выводятся в сообщения RuntimeHost.

## 4. Simulator bootstrap

`SimulatorSourceBootstrap` является отдельным тестируемым компонентом.

Он:

1. читает active manifest только для configured runtime scope;
2. отличает ожидаемое отсутствие manifest от fatal ошибки;
3. не выделяет session generation, пока active manifest отсутствует;
4. проверяет совпадение scope;
5. выделяет durable session generation;
6. создаёт `SimulatorPollingSource`.

Отсутствие active manifest возвращает состояние `NoActiveManifest`, а не останавливает процесс.

## 5. Bounded polling worker

`SimulatorPollingWorker` переиспользует существующий `BoundedPollScheduler`.

Worker:

- запускает первый poll немедленно;
- не запускает перекрывающийся poll одного source;
- учитывает scheduler capacity;
- не создаёт фиктивный cut при timeout;
- не принимает stale completion;
- передаёт только completed cut в bounded ingress;
- последовательно опустошает доступные obligations ограниченным batch;
- прекращает работу при закрытом admission или persistence failure;
- корректно завершается по cancellation.

Наблюдаемый snapshot worker содержит:

- lifecycle state;
- binding и schedule sequence;
- completed/timeout/stale counters;
- missed overlap/capacity counters;
- admitted cut count;
- processed obligation count;
- безопасный last error code;
- snapshot scheduler.

Simulator command lifecycle polling worker не вызывает.

## 6. Production lifecycle и failure policy

`RuntimeHostApplication` и `ProductionRuntimeHostSession` отделяют composition root от lifecycle logic.

При отсутствии active manifest:

- процесс остаётся живым;
- Core не пересоздаётся на каждом reconciliation attempt;
- применяется ограниченный exponential backoff;
- backoff ограничивается configured maximum;
- cancellation прерывает ожидание.

При временной ошибке PostgreSQL:

- текущая session освобождается;
- новые polls не запускаются;
- применяется bounded retry;
- создаётся новая production session;
- новый bootstrap получает новое durable source session generation.

Fatal semantic/invariant failure:

- не маскируется бесконечным retry;
- завершает приложение с безопасным error code.

Graceful shutdown:

1. cancellation останавливает polling worker;
2. вызывается `RuntimeProcess.StopAsync`;
3. protocol supervisor останавливается;
4. Core закрывает admission и выполняет drain;
5. `NpgsqlDataSource` освобождается.

## 7. Изменённые и созданные файлы

### Core

- `src/Dispatcher.Core/CoreRuntimeMigrations.cs`
- `src/Dispatcher.Core/CoreRuntimeStore.SessionGeneration.cs`

### RuntimeHost

- `src/Dispatcher.RuntimeHost/Dispatcher.RuntimeHost.csproj`
- `src/Dispatcher.RuntimeHost/Program.cs`
- `src/Dispatcher.RuntimeHost/ProductionRuntimeHostSession.cs`
- `src/Dispatcher.RuntimeHost/RuntimeHostApplication.cs`
- `src/Dispatcher.RuntimeHost/RuntimeHostOptions.cs`
- `src/Dispatcher.RuntimeHost/RuntimeProcess.cs`
- `src/Dispatcher.RuntimeHost/SimulatorPollingWorker.cs`
- `src/Dispatcher.RuntimeHost/SimulatorSourceBootstrap.cs`

### Unit tests

- `tests/Dispatcher.UnitTests/Dispatcher.UnitTests.csproj`
- `tests/Dispatcher.UnitTests/RuntimeHostApplicationTests.cs`
- `tests/Dispatcher.UnitTests/RuntimeHostOptionsTests.cs`
- `tests/Dispatcher.UnitTests/SimulatorPollingWorkerTests.cs`
- `tests/Dispatcher.UnitTests/SimulatorSourceBootstrapTests.cs`

### Integration tests

- `tests/Dispatcher.IntegrationTests/Dispatcher.IntegrationTests.csproj`
- `tests/Dispatcher.IntegrationTests/CoreRuntimeSessionGenerationTests.cs`
- `tests/Dispatcher.IntegrationTests/RuntimeHostSimulatorPollingIntegrationTests.cs`

## 8. Автоматически проверенные сценарии

Реализованы проверки:

- durable allocation сохраняется между экземплярами store;
- concurrent allocations уникальны и непрерывны;
- bounded settings валидируются;
- отсутствие active manifest не выделяет session;
- новая session fence-ит completion предыдущего bootstrap;
- completed poll проходит enqueue и последовательную обработку;
- timeout не создаёт RuntimeCut;
- admission failure останавливает worker;
- no-manifest retry использует capped backoff;
- transient startup failure пересоздаёт session;
- fatal cycle failure не повторяется;
- production Simulator worker записывает Core current;
- restart получает более новое session generation;
- после обработки pending obligations отсутствуют.

## 9. Финальная проверка C02

Финальная приёмка выполнена 27 июля 2026 года из корня repository:

```powershell
dotnet restore Dispatcher.slnx
```

```powershell
dotnet build Dispatcher.slnx -c Release --no-restore
```

```powershell
dotnet test tests\Dispatcher.UnitTests\Dispatcher.UnitTests.csproj -c Release --no-build
```

```powershell
dotnet test tests\Dispatcher.IntegrationTests\Dispatcher.IntegrationTests.csproj -c Release --no-build
```

Фактический результат:

- restore завершён успешно;
- Release-сборка всех проектов завершена успешно;
- `Dispatcher.RuntimeHost` собран успешно;
- Unit suite: 106 тестов, 106 успешно, 0 сбоев, 0 пропущено;
- Integration suite: 80 тестов, 80 успешно, 0 сбоев, 0 пропущено;
- суммарно проверено 186 тестов;
- новый production integration test RuntimeHost/Simulator/Core прошёл;
- информационное сообщение `NETSDK1057` о предварительной версии .NET не является ошибкой компилятора или анализатора.

C02 переведён в `Complete`. Непосредственная зависимость C03 переведена в `Ready`.
