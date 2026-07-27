using System.Text.Json;
using Dispatcher.Alarm;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Facilities;
using Dispatcher.History;
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

    public RuntimeHostSimulatorPollingIntegrationTests(
        PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task ProductionWorkerRunsDurableDownstreamPipelineAndRestarts()
    {
        await using var database =
            await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(
            database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            CoreRuntimeMigrations.CreatePlan(
                PostgreSqlClusterFixture.OwnerBRole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            HistoryMigrations.CreatePlan(
                PostgreSqlClusterFixture.OwnerBRole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            AlarmMigrations.CreatePlan(
                PostgreSqlClusterFixture.OwnerBRole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            EventMigrations.CreatePlan(
                PostgreSqlClusterFixture.OwnerBRole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            SimulatorRuntimeMigrations.CreatePlan(
                PostgreSqlClusterFixture.OwnerBRole));

        var wallClock = SystemClock.Instance;
        var simulatorStore = new SimulatorRuntimeStore(
            dataSource,
            PostgreSqlClusterFixture.OwnerBRole,
            wallClock);
        var revisionId =
            await ActivateManifestAsync(simulatorStore);
        var alarmStore = new AlarmStore(
            dataSource,
            PostgreSqlClusterFixture.OwnerBRole,
            wallClock);
        Assert.True(
            (await alarmStore.ActivateDefinitionSetAsync(
                new AlarmDefinitionSet(
                    RuntimeScopeId.From(ScopeValue),
                    RevisionNumber.Initial,
                    []))).IsSuccess);

        var options = CreateOptions(
            database.ConnectionString,
            revisionId);
        var monitor = new CoreRuntimeStore(
            dataSource,
            PostgreSqlClusterFixture.OwnerBRole,
            wallClock);

        var first = await RunUntilCheckpointAsync(
            options,
            monitor,
            minimumSessionGeneration: 1);
        var firstSource = Assert.Single(first.Sources);
        var firstCurrent = Assert.Single(first.Current);
        Assert.Equal(
            (ulong)1,
            firstSource.Binding.SessionGeneration.Value);
        Assert.Equal(
            SourceValue,
            firstSource.Binding.SourceId.Value);
        Assert.Equal(PointValue, firstCurrent.PointId.Value);
        Assert.Equal(42L, firstCurrent.Value.Value);
        Assert.Equal(
            (ulong)1,
            firstCurrent.SourcePosition.Value);

        var restarted = await RunUntilCheckpointAsync(
            options,
            monitor,
            minimumSessionGeneration: 2);
        var restartedSource =
            Assert.Single(restarted.Sources);
        var restartedCurrent =
            Assert.Single(restarted.Current);
        Assert.Equal(
            (ulong)2,
            restartedSource.Binding.SessionGeneration.Value);
        Assert.Equal(
            restartedSource.Binding.SessionGeneration,
            restartedCurrent.SessionGeneration);
        Assert.Equal(42L, restartedCurrent.Value.Value);
        Assert.True(
            restarted.CurrentPosition.Value >
            first.CurrentPosition.Value);
        Assert.True(
            restarted.LivenessPosition.Value >
            first.LivenessPosition.Value);
        Assert.Equal(
            0L,
            await monitor.CountPendingAsync(
                options.ScopeId));
        Assert.Null(
            await monitor.LoadPendingDeliveryAsync(
                options.ScopeId));

        var counts = await ReadPipelineCountsAsync(
            dataSource);
        Assert.True(counts.HistoryBatches >= 2);
        Assert.Equal(
            counts.HistoryBatches,
            counts.HistorySamples);
        Assert.True(counts.PublishedDeliveries >= 2);
        Assert.Equal(0L, counts.EventJournal);
        Assert.Equal(0L, counts.EventProjection);
    }

    private static async Task<ConfigurationRevisionId>
        ActivateManifestAsync(
            SimulatorRuntimeStore store)
    {
        var facilityScopeId =
            FacilityScopeId.From(ScopeValue);
        var revisionId =
            ConfigurationRevisionId.New();
        var manifest = JsonSerializer.Serialize(
            new
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
            RevisionNumber.Initial,
            null,
            manifest,
            ConfigurationManifestFingerprint
                .Normalize(manifest)
                .Fingerprint,
            new string('A', 64));

        Assert.True(
            (await store.ReceiveAsync(
                delivery,
                "runtime-test-receiver")).IsSuccess);
        Assert.True(
            (await store.ValidateAsync(
                facilityScopeId,
                revisionId,
                "runtime-test-validator")).IsSuccess);
        var active = await store.ActivateAsync(
            facilityScopeId,
            revisionId,
            "runtime-test-activator");
        Assert.True(active.IsSuccess);
        Assert.Equal(1, active.Value.Generation);
        return revisionId;
    }

    private static RuntimeHostOptions CreateOptions(
        string connectionString,
        ConfigurationRevisionId revisionId) =>
        new(
            RuntimeScopeId.From(ScopeValue),
            ProtocolWorkloadIdentity.From(
                "runtime-host-integration"),
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
            ReconciliationInitialBackoff:
                TimeSpan.FromMilliseconds(20),
            ReconciliationMaxBackoff:
                TimeSpan.FromMilliseconds(100))
        {
            Downstream = new RuntimeDownstreamOptions(
                PostgreSqlClusterFixture.OwnerBRole,
                PostgreSqlClusterFixture.OwnerBRole,
                PostgreSqlClusterFixture.OwnerBRole,
                revisionId.Value,
                RevisionNumber.Initial,
                historyMaxPageSize: 100,
                historyMaxAggregateBuckets: 100,
                eventMaxPageSize: 100,
                eventRetainedProjectionChanges: 100,
                eventMaxFeedChanges: 100,
                new RuntimeDownstreamRetryPolicy(
                    maxAttempts: 2,
                    initialBackoff:
                        TimeSpan.FromMilliseconds(10),
                    maximumBackoff:
                        TimeSpan.FromMilliseconds(20))),
        };

    private static async Task<CoreRuntimeCheckpoint>
        RunUntilCheckpointAsync(
            RuntimeHostOptions options,
            CoreRuntimeStore monitor,
            ulong minimumSessionGeneration)
    {
        await using var session =
            ProductionRuntimeHostSession.Create(
                options,
                SystemClock.Instance,
                SystemClock.Instance);
        using var operationTimeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(10));
        var started = await session.StartAsync(
            operationTimeout.Token);
        Assert.True(started.IsSuccess);

        using var workerCancellation =
            new CancellationTokenSource();
        var workerTask = session.RunSimulatorCycleAsync(
            workerCancellation.Token);
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
            _ = await workerTask.WaitAsync(
                TimeSpan.FromSeconds(5));
            _ = await session.StopAsync(
                CancellationToken.None);
            throw;
        }

        workerCancellation.Cancel();
        var cycle = await workerTask.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.True(
            cycle.IsSuccess,
            cycle.Error?.Code.Value);
        Assert.Equal(
            RuntimeHostSessionCycleStatus.WorkerStopped,
            cycle.Value.Status);
        var stopped = await session.StopAsync(
            CancellationToken.None);
        Assert.True(
            stopped.IsSuccess,
            $"Runtime session stop failed: {stopped.Error?.Code.Value}");
        return checkpoint;
    }

    private static async Task<CoreRuntimeCheckpoint>
        WaitForCheckpointAsync(
            CoreRuntimeStore store,
            RuntimeScopeId scopeId,
            ulong minimumSessionGeneration,
            CancellationToken cancellationToken)
    {
        while (true)
        {
            var recovery = await store.LoadRecoveryAsync(
                scopeId,
                cancellationToken);
            if (recovery.Checkpoint is { } checkpoint &&
                checkpoint.Current.Count > 0 &&
                checkpoint.Sources.Any(source =>
                    source.Binding.SessionGeneration.Value >=
                    minimumSessionGeneration))
            {
                return checkpoint;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(20),
                cancellationToken);
        }
    }

    private static async Task<PipelineCounts>
        ReadPipelineCountsAsync(
            NpgsqlDataSource dataSource)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using var setRole = new NpgsqlCommand(
            $"SET LOCAL ROLE \"{PostgreSqlClusterFixture.OwnerBRole}\";",
            connection,
            transaction);
        await setRole.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand(
            $"""
            SELECT
                (SELECT count(*)
                 FROM {HistoryMigrations.Schema}.ingest_batch),
                (SELECT count(*)
                 FROM {HistoryMigrations.Schema}.sample),
                (SELECT count(*)
                 FROM {CoreRuntimeMigrations.Schema}.processing_delivery
                 WHERE stage = 3),
                (SELECT count(*)
                 FROM {EventMigrations.Schema}.journal_event),
                (SELECT count(*)
                 FROM {EventMigrations.Schema}.occurrence_projection);
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new PipelineCounts(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    private sealed record PipelineCounts(
        long HistoryBatches,
        long HistorySamples,
        long PublishedDeliveries,
        long EventJournal,
        long EventProjection);
}
