using Dispatcher.Alarm;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Facilities;
using Dispatcher.History;
using Dispatcher.Modbus;
using Dispatcher.ProtocolCommissioning;
using Dispatcher.Protocols;
using Dispatcher.Semantics;
using Dispatcher.Simulator;
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
        IModbusTcpConnectionFactory modbusConnectionFactory)
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
    }

    public static ProductionRuntimeHostSession Create(
        RuntimeHostOptions options,
        IWallClock wallClock,
        IMonotonicClock monotonicClock,
        IModbusTcpConnectionFactory? modbusConnectionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(wallClock);
        ArgumentNullException.ThrowIfNull(monotonicClock);
        if (options.ProtocolMaxObservations < options.ModbusLimits.MaxPoints)
        {
            throw new InvalidOperationException(
                "Runtime protocol observation capacity must cover the configured Modbus point capacity.");
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
                    new(256, 128, 512)),
                maxProtocolSources: Math.Min(
                    options.MaxProtocolSources,
                    options.SchedulerMaxBindings - 1));
        return new ProductionRuntimeHostSession(
            dataSource,
            core,
            process,
            bootstrap,
            configurationReconciler,
            options,
            monotonicClock,
            wallClock,
            modbusConnectionFactory ?? new TcpModbusConnectionFactory());
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
                        return Result.Success(new RuntimeHostSessionCycleResult(
                            RuntimeHostSessionCycleStatus.NoActiveManifest));
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
            if (workers is not null)
            {
                _ = await workers.StopAsync().ConfigureAwait(false);
                await workers.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private Result<ActiveRuntimeWorkers> StartWorkers(
        ActivatedRuntimeConfiguration configuration,
        CancellationToken cancellationToken) =>
        StartWorkers(
            configuration.Source,
            configuration.ProtocolPlan,
            configuration.ModbusBindings,
            cancellationToken);

    private Result<ActiveRuntimeWorkers> StartWorkers(
        SimulatorPollingSource simulator,
        ProtocolActivationPlan? protocolPlan,
        IReadOnlyList<SourceBinding> modbusBindings,
        CancellationToken cancellationToken)
    {
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

        var registered = process.ReplaceProtocolSources(modbus.Value.Controllers);
        if (registered.IsFailure)
        {
            modbus.Value.Dispose();
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
        if (modbus.Value.Sources.Count > 0)
        {
            var protocolWorker = new ProtocolPollingWorker(
                modbus.Value.Sources,
                scheduler,
                options.PollInterval,
                maxProcessBatch,
                process);
            tasks.Add(protocolWorker.RunAsync(linked.Token));
        }

        return Result.Success(new ActiveRuntimeWorkers(
            linked,
            CompleteWorkersAsync(tasks),
            modbus.Value));
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
        private readonly ModbusRuntimeSourceSet modbus;
        private int disposed;

        public ActiveRuntimeWorkers(
            CancellationTokenSource cancellation,
            Task<Result> completion,
            ModbusRuntimeSourceSet modbus)
        {
            this.cancellation = cancellation;
            Completion = completion;
            this.modbus = modbus;
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
                modbus.Dispose();
                cancellation.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}
