using Dispatcher.Alarm;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.History;
using Dispatcher.Modbus;
using Dispatcher.ProtocolCommissioning;
using Dispatcher.Protocols;
using Dispatcher.Semantics;
using Dispatcher.Simulator;
using Dispatcher.Snmp;
using Npgsql;

namespace Dispatcher.RuntimeHost;

public sealed class ProductionRuntimeHostSession : IRuntimeHostSession
{
    private readonly NpgsqlDataSource dataSource;
    private readonly CoreRuntimeHost core;
    private readonly RuntimeProcess process;
    private readonly SimulatorSourceBootstrap bootstrap;
    private readonly RuntimeConfigurationReconciler? configurationReconciler;
    private readonly RuntimeHostOptions options;
    private readonly IMonotonicClock monotonicClock;
    private readonly IWallClock wallClock;
    private readonly IModbusTcpConnectionFactory modbusConnectionFactory;
    private readonly ISnmpDatagramClientFactory snmpClientFactory;
    private readonly IProtocolSecretResolver protocolSecretResolver;
    private readonly EquipmentDiagnosticWorker? diagnosticWorker;
    private bool started;
    private bool stopped;

    private ProductionRuntimeHostSession(
        NpgsqlDataSource dataSource,
        CoreRuntimeHost core,
        RuntimeProcess process,
        SimulatorSourceBootstrap bootstrap,
        RuntimeConfigurationReconciler? configurationReconciler,
        RuntimeHostOptions options,
        IMonotonicClock monotonicClock,
        IWallClock wallClock,
        IModbusTcpConnectionFactory modbusConnectionFactory,
        ISnmpDatagramClientFactory snmpClientFactory,
        IProtocolSecretResolver protocolSecretResolver,
        EquipmentDiagnosticWorker? diagnosticWorker)
    {
        this.dataSource = dataSource;
        this.core = core;
        this.process = process;
        this.bootstrap = bootstrap;
        this.configurationReconciler = configurationReconciler;
        this.options = options;
        this.monotonicClock = monotonicClock;
        this.wallClock = wallClock;
        this.modbusConnectionFactory = modbusConnectionFactory;
        this.snmpClientFactory = snmpClientFactory;
        this.protocolSecretResolver = protocolSecretResolver;
        this.diagnosticWorker = diagnosticWorker;
    }

    public static ProductionRuntimeHostSession Create(
        RuntimeHostOptions options,
        IWallClock wallClock,
        IMonotonicClock monotonicClock,
        IModbusTcpConnectionFactory? modbusConnectionFactory = null,
        ISnmpDatagramClientFactory? snmpClientFactory = null,
        IProtocolSecretResolver? protocolSecretResolver = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(wallClock);
        ArgumentNullException.ThrowIfNull(monotonicClock);
        if (options.ProtocolMaxObservations <
            Math.Max(options.ModbusLimits.MaxPoints, options.SnmpLimits.MaxPoints))
        {
            throw new InvalidOperationException(
                "Runtime protocol observation capacity must cover configured protocol point capacity.");
        }

        if (options.ProtocolMaxResponseBytes <= 4)
        {
            throw new InvalidOperationException(
                "Runtime protocol response capacity must contain the bounded SNMP response envelope.");
        }

        var downstream = options.Downstream ??
            throw new InvalidOperationException(
                "Production RuntimeHost requires explicit downstream processing settings.");

        var dataSource =
            NpgsqlDataSource.Create(options.ConnectionString);
        var store = new CoreRuntimeStore(
            dataSource,
            options.DatabaseRole,
            wallClock);
        var historyStore = new HistoryStore(
            dataSource,
            downstream.HistoryDatabaseRole,
            wallClock,
            new HistoryQueryLimits(
                downstream.HistoryMaxPageSize,
                downstream.HistoryMaxAggregateBuckets));
        var alarmStore = new AlarmStore(
            dataSource,
            downstream.AlarmDatabaseRole,
            wallClock);
        var eventStore = new EventStore(
            dataSource,
            downstream.EventDatabaseRole,
            wallClock,
            new EventDispatcherLimits(
                downstream.EventMaxPageSize,
                downstream.EventRetainedProjectionChanges,
                downstream.EventMaxFeedChanges));
        var definitionBinding = new RuntimeDefinitionBindingState(
            downstream.ConfigurationRevisionId,
            downstream.AlarmDefinitionEpoch);
        var coordinator = new RuntimeDeliveryCoordinator(
            store,
            new RuntimeHistoryDeliveryProcessor(
                historyStore,
                store),
            new RuntimeAlarmDeliveryProcessor(
                alarmStore,
                store),
            new RuntimeEventDeliveryProcessor(
                alarmStore,
                eventStore,
                store),
            definitionBinding,
            downstream.RetryPolicy,
            IsTransientDownstreamFailure);
        var core = new CoreRuntimeHost(
            options.ScopeId,
            new CoreRuntime(
                options.ScopeId,
                wallClock,
                monotonicClock,
                new RuntimeCurrentLimits(
                    options.MaxCurrentPoints,
                    options.RetainedCurrentChanges)),
            store,
            new RuntimeIngressLimits(
                options.IngressCapacity),
            deliveryProcessor:
                coordinator.CreateDeliveryProcessor());
        var protocols = new ProtocolRuntimeSupervisor(
            options.WorkloadIdentity,
            options.MaxProtocolSources);
        var process = new RuntimeProcess(core, protocols);
        var simulatorStore = new SimulatorRuntimeStore(
            dataSource,
            options.SimulatorDatabaseRole,
            wallClock);
        var bootstrap = new SimulatorSourceBootstrap(
            options.ScopeId,
            simulatorStore.ReadActiveAsync,
            store.AllocateSourceSessionGenerationAsync,
            wallClock);
        var configurationReconciler = options.ConfigurationDatabaseRole is null
            ? null
            : new RuntimeConfigurationReconciler(
                FacilityScopeId.From(options.ScopeId.Value),
                options.WorkloadIdentity.Value,
                options.DeploymentLeaseDuration,
                new ConfigurationWorkloadDeploymentStore(
                    dataSource,
                    options.ConfigurationDatabaseRole,
                    wallClock),
                simulatorStore,
                alarmStore,
                store.AllocateSourceSessionGenerationAsync,
                definitionBinding,
                wallClock,
                protocolLimits: new ProtocolCommissioningLimits(
                    options.ModbusLimits,
                    options.SnmpLimits),
                maxProtocolSources: Math.Min(
                    options.MaxProtocolSources,
                    options.SchedulerMaxBindings - 1));
        if ((options.EquipmentDatabaseRole is null) != (options.StagingSecretKey is null))
        {
            throw new InvalidOperationException(
                "Runtime equipment diagnostic role and staging secret key must be configured together.");
        }

        var actualModbusFactory = modbusConnectionFactory ?? new TcpModbusConnectionFactory();
        var actualSnmpFactory = snmpClientFactory ?? new UdpSnmpDatagramClientFactory();
        var environmentResolver = protocolSecretResolver ??
            new EnvironmentProtocolSecretResolver(options.WorkloadIdentity);
        IProtocolSecretResolver actualSecretResolver = environmentResolver;
        EquipmentDiagnosticWorker? diagnosticWorker = null;
        if (options.EquipmentDatabaseRole is not null && options.StagingSecretKey is not null)
        {
            var equipmentStaging = new EquipmentStagingStore(
                dataSource,
                options.EquipmentDatabaseRole,
                wallClock);
            var databaseResolver = new DatabaseProtocolSecretResolver(
                equipmentStaging,
                FacilityScopeId.From(options.ScopeId.Value),
                new StagingSecretProtector(options.StagingSecretKey),
                options.WorkloadIdentity);
            actualSecretResolver = new CompositeProtocolSecretResolver(
                environmentResolver,
                databaseResolver);
            diagnosticWorker = new EquipmentDiagnosticWorker(
                equipmentStaging,
                FacilityScopeId.From(options.ScopeId.Value),
                options.WorkloadIdentity.Value,
                options.DiagnosticLeaseDuration,
                options.DiagnosticPollInterval,
                new ProtocolCommissioningLimits(options.ModbusLimits, options.SnmpLimits),
                options.SnmpWireLimits,
                options.WorkloadIdentity,
                options.CreateProtocolIoLimits(),
                actualSecretResolver,
                actualModbusFactory,
                actualSnmpFactory,
                wallClock);
        }

        return new ProductionRuntimeHostSession(
            dataSource,
            core,
            process,
            bootstrap,
            configurationReconciler,
            options,
            monotonicClock,
            wallClock,
            actualModbusFactory,
            actualSnmpFactory,
            actualSecretResolver,
            diagnosticWorker);
    }

    public async Task<Result> StartAsync(
        CancellationToken cancellationToken)
    {
        if (started)
        {
            return Failure(
                "runtime.session_state",
                "Production runtime session can only start once.");
        }

        var result = await process.StartAsync(
            cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            started = true;
        }

        return result;
    }

    public async Task<Result<RuntimeHostSessionCycleResult>>
        RunSimulatorCycleAsync(
            CancellationToken cancellationToken)
    {
        if (!started || stopped)
        {
            return Failure<RuntimeHostSessionCycleResult>(
                "runtime.session_state",
                "Production runtime session is not running.");
        }

        if (configurationReconciler is not null)
        {
            return await RunConfigurationReconciliationAsync(cancellationToken).ConfigureAwait(false);
        }

        var reconciled = await bootstrap.ReconcileAsync(
            cancellationToken).ConfigureAwait(false);
        if (reconciled.IsFailure)
        {
            return Result.Failure<RuntimeHostSessionCycleResult>(
                reconciled.Error!);
        }

        if (reconciled.Value.Status ==
            SimulatorSourceReconciliationStatus.NoActiveManifest)
        {
            return Result.Success(
                new RuntimeHostSessionCycleResult(
                    RuntimeHostSessionCycleStatus.NoActiveManifest));
        }

        var scheduler = new BoundedPollScheduler(
            options.ScopeId,
            options.CreatePollScheduleLimits(),
            monotonicClock);
        var maxProcessBatch =
            options.IngressCapacity == int.MaxValue
                ? int.MaxValue
                : options.IngressCapacity + 1;
        var worker = new SimulatorPollingWorker(
            reconciled.Value.Source!,
            scheduler,
            options.PollInterval,
            maxProcessBatch,
            process.ActivateSimulatorBinding,
            process.EnqueueAsync,
            process.ProcessNextAsync);
        var result = await worker.RunAsync(
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Result.Success(
                new RuntimeHostSessionCycleResult(
                    RuntimeHostSessionCycleStatus.WorkerStopped))
            : Result.Failure<RuntimeHostSessionCycleResult>(
                result.Error!);
    }

    private async Task<Result<RuntimeHostSessionCycleResult>> RunConfigurationReconciliationAsync(
        CancellationToken cancellationToken)
    {
        using var diagnosticCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var diagnosticTask = diagnosticWorker?.RunAsync(diagnosticCancellation.Token);
        ActivatedRuntimeConfiguration? activeConfiguration = null;
        SimulatorPollingSource? legacySource = null;
        var restored = await configurationReconciler!.RestoreAsync(cancellationToken).ConfigureAwait(false);
        if (restored.IsSuccess)
        {
            activeConfiguration = restored.Value;
        }
        else if (restored.Error?.Code.Value == "configuration.workload_activation_not_found")
        {
            var legacy = await bootstrap.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            if (legacy.IsFailure &&
                legacy.Error?.Code.Value != "simulator.active_not_found")
            {
                return Result.Failure<RuntimeHostSessionCycleResult>(legacy.Error!);
            }

            legacySource = legacy.IsSuccess &&
                legacy.Value.Status == SimulatorSourceReconciliationStatus.Ready
                    ? legacy.Value.Source
                    : null;
        }
        else
        {
            return Result.Failure<RuntimeHostSessionCycleResult>(restored.Error!);
        }

        ActiveRuntimeWorkers? workers = null;
        try
        {
            if (activeConfiguration is not null)
            {
                var started = StartWorkers(activeConfiguration, cancellationToken);
                if (started.IsFailure)
                {
                    return Result.Failure<RuntimeHostSessionCycleResult>(started.Error!);
                }

                workers = started.Value;
            }
            else if (legacySource is not null)
            {
                var started = StartWorkers(
                    legacySource,
                    null,
                    [],
                    cancellationToken);
                if (started.IsFailure)
                {
                    return Result.Failure<RuntimeHostSessionCycleResult>(started.Error!);
                }

                workers = started.Value;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (diagnosticTask?.IsCompleted == true)
                {
                    var diagnosticResult = await diagnosticTask.ConfigureAwait(false);
                    if (diagnosticResult.IsFailure)
                    {
                        return Result.Failure<RuntimeHostSessionCycleResult>(diagnosticResult.Error!);
                    }
                }

                if (workers is not null)
                {
                    var interval = Task.Delay(
                        options.ConfigurationReconciliationInterval,
                        cancellationToken);
                    var completed = await Task.WhenAny(workers.Completion, interval).ConfigureAwait(false);
                    if (completed == workers.Completion)
                    {
                        var worker = await workers.Completion.ConfigureAwait(false);
                        return worker.IsSuccess
                            ? Result.Success(new RuntimeHostSessionCycleResult(
                                RuntimeHostSessionCycleStatus.WorkerStopped))
                            : Result.Failure<RuntimeHostSessionCycleResult>(worker.Error!);
                    }
                }

                var reconciliation = await configurationReconciler.PrepareNextAsync(
                    cancellationToken).ConfigureAwait(false);
                if (reconciliation.IsFailure)
                {
                    return Result.Failure<RuntimeHostSessionCycleResult>(reconciliation.Error!);
                }

                if (reconciliation.Value.Status != RuntimeConfigurationReconciliationStatus.Prepared)
                {
                    if (workers is null)
                    {
                        if (diagnosticTask is null)
                        {
                            return Result.Success(new RuntimeHostSessionCycleResult(
                                RuntimeHostSessionCycleStatus.NoActiveManifest));
                        }

                        await Task.Delay(
                            options.ConfigurationReconciliationInterval,
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    continue;
                }

                if (workers is not null)
                {
                    var drained = await workers.StopAsync().ConfigureAwait(false);
                    await workers.DisposeAsync().ConfigureAwait(false);
                    workers = null;
                    if (drained.IsFailure)
                    {
                        return Result.Failure<RuntimeHostSessionCycleResult>(drained.Error!);
                    }
                }

                var activated = await configurationReconciler.CommitAsync(
                    reconciliation.Value.Prepared!,
                    cancellationToken).ConfigureAwait(false);
                if (activated.IsFailure)
                {
                    return Result.Failure<RuntimeHostSessionCycleResult>(activated.Error!);
                }

                var started = StartWorkers(activated.Value, cancellationToken);
                if (started.IsFailure)
                {
                    return Result.Failure<RuntimeHostSessionCycleResult>(started.Error!);
                }

                workers = started.Value;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result.Success(new RuntimeHostSessionCycleResult(
                RuntimeHostSessionCycleStatus.WorkerStopped));
        }
        finally
        {
            diagnosticCancellation.Cancel();
            if (workers is not null)
            {
                _ = await workers.StopAsync().ConfigureAwait(false);
                await workers.DisposeAsync().ConfigureAwait(false);
            }

            if (diagnosticTask is not null)
            {
                _ = await diagnosticTask.ConfigureAwait(false);
            }
        }
    }

    private Result<ActiveRuntimeWorkers> StartWorkers(
        ActivatedRuntimeConfiguration configuration,
        CancellationToken cancellationToken) =>
        StartWorkers(
            configuration.Source,
            configuration.ProtocolPlan,
            configuration.ProtocolBindings,
            cancellationToken);

    private Result<ActiveRuntimeWorkers> StartWorkers(
        SimulatorPollingSource simulator,
        ProtocolActivationPlan? protocolPlan,
        IReadOnlyList<SourceBinding> protocolBindings,
        CancellationToken cancellationToken)
    {
        var modbusSourceIds = protocolPlan?.ModbusSources
            .Select(source => source.SourceId)
            .ToHashSet() ?? [];
        var snmpSourceIds = protocolPlan?.SnmpSources
            .Select(source => source.SourceId)
            .ToHashSet() ?? [];
        var modbusBindings = protocolBindings
            .Where(binding => modbusSourceIds.Contains(binding.SourceId))
            .ToArray();
        var snmpBindings = protocolBindings
            .Where(binding => snmpSourceIds.Contains(binding.SourceId))
            .ToArray();
        if (modbusBindings.Length + snmpBindings.Length != protocolBindings.Count)
        {
            return Failure<ActiveRuntimeWorkers>(
                "runtime.protocol_binding_plan",
                "Protocol source bindings do not match the activation plan.");
        }

        var modbus = ModbusRuntimeSourceFactory.Create(
            protocolPlan,
            modbusBindings,
            options.ModbusLimits,
            options.WorkloadIdentity,
            options.CreateProtocolIoLimits(),
            modbusConnectionFactory,
            wallClock);
        if (modbus.IsFailure)
        {
            return Result.Failure<ActiveRuntimeWorkers>(modbus.Error!);
        }

        var snmp = SnmpRuntimeSourceFactory.Create(
            protocolPlan,
            snmpBindings,
            options.SnmpLimits,
            options.SnmpWireLimits,
            options.WorkloadIdentity,
            options.CreateProtocolIoLimits(),
            protocolSecretResolver,
            snmpClientFactory,
            wallClock);
        if (snmp.IsFailure)
        {
            modbus.Value.Dispose();
            return Result.Failure<ActiveRuntimeWorkers>(snmp.Error!);
        }

        var controllers = modbus.Value.Controllers
            .Concat(snmp.Value.Controllers)
            .ToDictionary(item => item.Key, item => item.Value);
        var registered = process.ReplaceProtocolSources(controllers);
        if (registered.IsFailure)
        {
            modbus.Value.Dispose();
            snmp.Value.Dispose();
            return Result.Failure<ActiveRuntimeWorkers>(registered.Error!);
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var scheduler = new BoundedPollScheduler(
            options.ScopeId,
            options.CreatePollScheduleLimits(),
            monotonicClock);
        var maxProcessBatch =
            options.IngressCapacity == int.MaxValue
                ? int.MaxValue
                : options.IngressCapacity + 1;
        var simulatorWorker = new SimulatorPollingWorker(
            simulator,
            scheduler,
            options.PollInterval,
            maxProcessBatch,
            process.ActivateSimulatorBinding,
            process.EnqueueAsync,
            process.ProcessNextAsync);
        var tasks = new List<Task<Result>>
        {
            simulatorWorker.RunAsync(linked.Token),
        };
        var protocolSources = modbus.Value.Sources
            .Cast<IRuntimeProtocolSource>()
            .Concat(snmp.Value.Sources)
            .ToArray();
        if (protocolSources.Length > 0)
        {
            var protocolWorker = new ProtocolPollingWorker(
                protocolSources,
                scheduler,
                options.PollInterval,
                maxProcessBatch,
                process);
            tasks.Add(protocolWorker.RunAsync(linked.Token));
        }

        return Result.Success(new ActiveRuntimeWorkers(
            linked,
            CompleteWorkersAsync(tasks),
            [modbus.Value, snmp.Value]));
    }

    private static async Task<Result> CompleteWorkersAsync(
        IReadOnlyList<Task<Result>> tasks)
    {
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.FirstOrDefault(result => result.IsFailure) ?? Result.Success();
    }

    private (CancellationTokenSource Cancellation, Task<Result> Task) StartWorker(
        SimulatorPollingSource source,
        CancellationToken cancellationToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var scheduler = new BoundedPollScheduler(
            options.ScopeId,
            options.CreatePollScheduleLimits(),
            monotonicClock);
        var maxProcessBatch =
            options.IngressCapacity == int.MaxValue
                ? int.MaxValue
                : options.IngressCapacity + 1;
        var worker = new SimulatorPollingWorker(
            source,
            scheduler,
            options.PollInterval,
            maxProcessBatch,
            process.ActivateSimulatorBinding,
            process.EnqueueAsync,
            process.ProcessNextAsync);
        return (linked, worker.RunAsync(linked.Token));
    }

    public async Task<Result> StopAsync(
        CancellationToken cancellationToken)
    {
        if (!started)
        {
            return Failure(
                "runtime.session_state",
                "Production runtime session was not started.");
        }

        if (stopped)
        {
            return Result.Success();
        }

        var result = await process.StopAsync(
            cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            stopped = true;
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        core.Dispose();
        await dataSource.DisposeAsync().ConfigureAwait(false);
    }

    private static bool IsTransientDownstreamFailure(
        Exception exception) =>
        exception is NpgsqlException or
            TimeoutException or
            IOException;

    private static Result Failure(
        string code,
        string message) =>
        Result.Failure(
            new OperationError(
                ErrorCode.From(code),
                message));

    private static Result<TValue> Failure<TValue>(
        string code,
        string message) =>
        Result.Failure<TValue>(
            new OperationError(
                ErrorCode.From(code),
                message));

    private sealed class ActiveRuntimeWorkers : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellation;
        private readonly IReadOnlyList<IDisposable> protocolSources;
        private int disposed;

        public ActiveRuntimeWorkers(
            CancellationTokenSource cancellation,
            Task<Result> completion,
            IReadOnlyList<IDisposable> protocolSources)
        {
            this.cancellation = cancellation;
            Completion = completion;
            this.protocolSources = protocolSources;
        }

        public Task<Result> Completion { get; }

        public async Task<Result> StopAsync()
        {
            cancellation.Cancel();
            return await Completion.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                foreach (var source in protocolSources)
                {
                    source.Dispose();
                }

                cancellation.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}
