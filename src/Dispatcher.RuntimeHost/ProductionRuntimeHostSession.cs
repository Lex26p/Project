using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.History;
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
    private readonly RuntimeHostOptions options;
    private readonly IMonotonicClock monotonicClock;
    private bool started;
    private bool stopped;

    private ProductionRuntimeHostSession(
        NpgsqlDataSource dataSource,
        CoreRuntimeHost core,
        RuntimeProcess process,
        SimulatorSourceBootstrap bootstrap,
        RuntimeHostOptions options,
        IMonotonicClock monotonicClock)
    {
        this.dataSource = dataSource;
        this.core = core;
        this.process = process;
        this.bootstrap = bootstrap;
        this.options = options;
        this.monotonicClock = monotonicClock;
    }

    public static ProductionRuntimeHostSession Create(
        RuntimeHostOptions options,
        IWallClock wallClock,
        IMonotonicClock monotonicClock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(wallClock);
        ArgumentNullException.ThrowIfNull(monotonicClock);
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
            downstream.ConfigurationRevisionId,
            downstream.AlarmDefinitionEpoch,
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
        _ = new EnvironmentProtocolSecretResolver(
            options.WorkloadIdentity);
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
        return new ProductionRuntimeHostSession(
            dataSource,
            core,
            process,
            bootstrap,
            options,
            monotonicClock);
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
}
