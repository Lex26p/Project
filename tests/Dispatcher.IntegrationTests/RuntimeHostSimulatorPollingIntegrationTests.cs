using System.Text.Json;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Facilities;
using Dispatcher.Persistence;
using Dispatcher.Protocols;
using Dispatcher.RuntimeHost;
using Dispatcher.Semantics;
using Dispatcher.Simulator;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class RuntimeHostSimulatorPollingIntegrationTests
{
    private static readonly Guid ScopeValue =
        Guid.Parse("b6000000-0000-0000-0000-000000000001");
    private static readonly Guid SourceValue =
        Guid.Parse("b7000000-0000-0000-0000-000000000001");
    private static readonly Guid PointValue =
        Guid.Parse("b8000000-0000-0000-0000-000000000001");
    private readonly PostgreSqlClusterFixture cluster;

    public RuntimeHostSimulatorPollingIntegrationTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task ProductionWorkerChangesCoreCurrentAndRestartAllocatesNewSession()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            CoreRuntimeMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            SimulatorRuntimeMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));

        var wallClock = SystemClock.Instance;
        var simulatorStore = new SimulatorRuntimeStore(
            dataSource,
            PostgreSqlClusterFixture.OwnerBRole,
            wallClock);
        await ActivateManifestAsync(simulatorStore);

        var options = CreateOptions(database.ConnectionString);
        var monitor = new CoreRuntimeStore(
            dataSource,
            PostgreSqlClusterFixture.OwnerBRole,
            wallClock);

        var first = await RunUntilCheckpointAsync(options, monitor, minimumSessionGeneration: 1);
        var firstSource = Assert.Single(first.Sources);
        var firstCurrent = Assert.Single(first.Current);
        Assert.Equal((ulong)1, firstSource.Binding.SessionGeneration.Value);
        Assert.Equal(SourceValue, firstSource.Binding.SourceId.Value);
        Assert.Equal(PointValue, firstCurrent.PointId.Value);
        Assert.Equal(42L, firstCurrent.Value.Value);
        Assert.Equal((ulong)1, firstCurrent.SourcePosition.Value);

        var restarted = await RunUntilCheckpointAsync(options, monitor, minimumSessionGeneration: 2);
        var restartedSource = Assert.Single(restarted.Sources);
        var restartedCurrent = Assert.Single(restarted.Current);
        Assert.Equal((ulong)2, restartedSource.Binding.SessionGeneration.Value);
        Assert.Equal(
            restartedSource.Binding.SessionGeneration,
            restartedCurrent.SessionGeneration);
        Assert.Equal(42L, restartedCurrent.Value.Value);
        Assert.True(restarted.CurrentPosition.Value > first.CurrentPosition.Value);
        Assert.True(restarted.LivenessPosition.Value > first.LivenessPosition.Value);
        Assert.Equal(0L, await monitor.CountPendingAsync(options.ScopeId));
    }

    private static async Task ActivateManifestAsync(SimulatorRuntimeStore store)
    {
        var facilityScopeId = FacilityScopeId.From(ScopeValue);
        var revisionId = ConfigurationRevisionId.New();
        var manifest = JsonSerializer.Serialize(new
        {
            simulator = new
            {
                sourceId = SourceValue,
                seed = 17UL,
                points = new[]
                {
                    new
                    {
                        pointId = PointValue,
                        baseline = 42L,
                        amplitude = 0L,
                        unit = "kW",
                    },
                },
            },
        });
        var delivery = new SimulatorManifestDelivery(
            revisionId,
            facilityScopeId,
            RevisionNumber.From(1),
            null,
            manifest,
            ConfigurationManifestFingerprint.Normalize(manifest).Fingerprint,
            new string('A', 64));

        Assert.True((await store.ReceiveAsync(delivery, "runtime-test-receiver")).IsSuccess);
        Assert.True((await store.ValidateAsync(
            facilityScopeId,
            revisionId,
            "runtime-test-validator")).IsSuccess);
        var active = await store.ActivateAsync(
            facilityScopeId,
            revisionId,
            "runtime-test-activator");
        Assert.True(active.IsSuccess);
        Assert.Equal(1, active.Value.Generation);
    }

    private static RuntimeHostOptions CreateOptions(string connectionString) => new(
        RuntimeScopeId.From(ScopeValue),
        ProtocolWorkloadIdentity.From("runtime-host-integration"),
        connectionString,
        PostgreSqlClusterFixture.OwnerBRole,
        PostgreSqlClusterFixture.OwnerBRole,
        MaxCurrentPoints: 8,
        RetainedCurrentChanges: 32,
        IngressCapacity: 4,
        MaxProtocolSources: 1,
        PollInterval: TimeSpan.FromSeconds(5),
        PollTimeout: TimeSpan.FromSeconds(2),
        SchedulerMaxBindings: 1,
        SchedulerMaxInFlight: 1,
        ReconciliationInitialBackoff: TimeSpan.FromMilliseconds(20),
        ReconciliationMaxBackoff: TimeSpan.FromMilliseconds(100));

    private static async Task<CoreRuntimeCheckpoint> RunUntilCheckpointAsync(
        RuntimeHostOptions options,
        CoreRuntimeStore monitor,
        ulong minimumSessionGeneration)
    {
        await using var session = ProductionRuntimeHostSession.Create(
            options,
            SystemClock.Instance,
            SystemClock.Instance);
        using var operationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = await session.StartAsync(operationTimeout.Token);
        Assert.True(started.IsSuccess);

        using var workerCancellation = new CancellationTokenSource();
        var workerTask = session.RunSimulatorCycleAsync(workerCancellation.Token);
        CoreRuntimeCheckpoint checkpoint;
        try
        {
            checkpoint = await WaitForCheckpointAsync(
                monitor,
                options.ScopeId,
                minimumSessionGeneration,
                operationTimeout.Token);
        }
        catch
        {
            workerCancellation.Cancel();
            _ = await workerTask.WaitAsync(TimeSpan.FromSeconds(5));
            _ = await session.StopAsync(CancellationToken.None);
            throw;
        }

        workerCancellation.Cancel();
        var cycle = await workerTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cycle.IsSuccess);
        Assert.Equal(RuntimeHostSessionCycleStatus.WorkerStopped, cycle.Value.Status);
        Assert.True((await session.StopAsync(CancellationToken.None)).IsSuccess);
        return checkpoint;
    }

    private static async Task<CoreRuntimeCheckpoint> WaitForCheckpointAsync(
        CoreRuntimeStore store,
        RuntimeScopeId scopeId,
        ulong minimumSessionGeneration,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var recovery = await store.LoadRecoveryAsync(scopeId, cancellationToken);
            if (recovery.Checkpoint is { } checkpoint &&
                checkpoint.Current.Count > 0 &&
                checkpoint.Sources.Any(source =>
                    source.Binding.SessionGeneration.Value >= minimumSessionGeneration))
            {
                return checkpoint;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }
}
