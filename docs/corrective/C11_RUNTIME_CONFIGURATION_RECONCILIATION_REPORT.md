# C11 — Workload configuration reconciliation и activation: итоговый отчёт

## 1. Результат

RuntimeHost больше не зависит от ручной Simulator activation через Server. Опубликованная whole-scope configuration revision доставляется и активируется отдельным workload flow:

1. RuntimeHost claims текущую опубликованную revision с lease token.
2. Проверяет immutable manifest, release/dependency fingerprints и строит целый activation plan.
3. Подготавливает и валидирует Simulator manifest, Alarm definitions и protocol extension plan.
4. Останавливает и дожидается старого Simulator worker.
5. Переключает Simulator generation и точный Alarm definition epoch.
6. Записывает switch и только после фактической activation выполняет acknowledgement.
7. Запускает новый worker с новой binding/session generation.

Новая Simulator revision начинает публиковать новые значения в том же процессе RuntimeHost без restart.

## 2. Authority и persistence

- Добавлен отдельный `ConfigurationWorkloadDeploymentStore`, не принимающий user session.
- User flow ограничен save/validate/publish/rollback; прежний Server `SimulatorReleaseActivator` удалён.
- Distribution/activation methods старого store оставлены только внутренними для совместимости owner implementation и исключены из user service.
- Configuration migration v3 хранит lease token, prepare/switch/ack timestamps, runtime generation, Alarm epoch и bounded sanitised outcome.
- Подготовленная activation блокирует конкурирующую публикацию до acknowledgement.
- Устаревший или superseded lease не может выполнить prepare/switch/ack.
- Secret values не входят в workload outcome и event journal.

## 3. Runtime activation

- Добавлен whole-scope activation plan для Simulator, Alarm и будущих Modbus/SNMP extension points без protocol I/O.
- Alarm definitions читаются из точной revision; пустой набор разрешён.
- Alarm epoch совпадает с runtime generation и меняется перед запуском нового source worker.
- `RuntimeDefinitionBindingState` переключает configuration revision и Alarm epoch для downstream delivery только после acknowledgement.
- Старый worker детерминированно останавливается до switch, новый запускается после ack.
- Core fencing отклоняет поздний poll прежней binding/session generation.
- Bounded reconciliation interval и deployment lease задаются RuntimeHost settings.
- RuntimeHost не вызывает Server по HTTP.

## 4. Recovery и отказные сценарии

Проверены:

- crash после claim;
- crash после prepare до switch;
- crash после switch до acknowledgement;
- invalid manifest;
- invalid release fingerprint;
- новая invalid revision при сохранении старой active generation;
- stale worker lease;
- stale poll старой binding после switch;
- повторная activation без двойного runtime switch;
- публикация новой revision без restart RuntimeHost;
- безопасная остановка faulted Core с сохранением durable obligations для restart recovery.

При crash lease reclaim повторяет idempotent receive/validate/switch и не увеличивает runtime generation повторно.

## 5. Изменённые области

### Production

- `src/Dispatcher.Configuration/ConfigurationModels.cs`
- `src/Dispatcher.Configuration/ConfigurationMigrations.cs`
- `src/Dispatcher.Configuration/ConfigurationService.cs`
- `src/Dispatcher.Configuration/ConfigurationStore.cs`
- `src/Dispatcher.Configuration/ConfigurationWorkloadDeploymentStore.cs`
- `src/Dispatcher.Core/CoreRuntimeHost.cs`
- `src/Dispatcher.RuntimeHost/Dispatcher.RuntimeHost.csproj`
- `src/Dispatcher.RuntimeHost/RuntimeHostOptions.cs`
- `src/Dispatcher.RuntimeHost/RuntimeDefinitionBindingState.cs`
- `src/Dispatcher.RuntimeHost/RuntimeConfigurationActivationPlan.cs`
- `src/Dispatcher.RuntimeHost/RuntimeConfigurationReconciler.cs`
- `src/Dispatcher.RuntimeHost/RuntimeDeliveryCoordinator.cs`
- `src/Dispatcher.RuntimeHost/ProductionRuntimeHostSession.cs`
- удалён `src/Dispatcher.Server/SimulatorReleaseActivator.cs`
- обновлены lock-файлы зависимого project-reference graph.

### Tests

- `tests/Dispatcher.IntegrationTests/RuntimeConfigurationReconciliationTests.cs`
- `tests/Dispatcher.IntegrationTests/ConfigurationRevisionTests.cs`
- `tests/Dispatcher.IntegrationTests/SimulatorActivationTests.cs`
- `tests/Dispatcher.IntegrationTests/ProtocolCommissioningAcceptanceTests.cs`
- `tests/Dispatcher.IntegrationTests/CoreRuntimeDurableDeliveryRecoveryTests.cs`
- `tests/Dispatcher.IntegrationTests/Process/SimulatorProcessE2ETests.cs`
- `tests/Dispatcher.UnitTests/RuntimeHostOptionsTests.cs`

## 6. Проверки

- `dotnet restore Dispatcher.slnx --locked-mode` — успешно после штатного обновления lock-файлов project-reference graph;
- `dotnet build Dispatcher.slnx --configuration Release --no-restore --maxcpucount:1 --nodeReuse:false` — успешно, 0 warnings, 0 errors;
- Unit — 139/139 успешно;
- новые C11 Integration — 8/8 успешно;
- C11 и затронутые configuration/simulator/protocol regression tests — 15/15 успешно;
- полный Integration — 128/129 в исходном прогоне; единственный старый C07 process test потребовал согласовать publish/condition timeouts и перенести Alarm lock после новой workload activation;
- скорректированный C07 process E2E — 1/1 успешно;
- faulted Core shutdown/recovery — 1/1 успешно.

## 7. Ограничения

- Modbus/SNMP source factories и physical I/O не реализовывались; activation plan содержит только extension point для C12/C13.
- Engineering Web чтение sanitised outcomes остаётся задачей последующего инженерного UI.

## 8. Итог

Критерии C11 выполнены. User publish и workload activation разделены, prepare/switch/ack восстанавливается после crash, invalid revision сохраняет последнюю рабочую generation, а Simulator и Alarm переключаются согласованно без restart RuntimeHost.
