using Dispatcher.Core;
using Dispatcher.Persistence;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class CoreRuntimeDurableDeliveryRecoveryTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 27, 10, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public CoreRuntimeDurableDeliveryRecoveryTests(
        PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task ProcessNextReturnsPublishedDeliveryOutcome()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var bundle = context.CreateHost();
        using (bundle.Host)
        {
            Assert.True((await bundle.Host.StartAsync()).IsSuccess);
            Assert.True(bundle.Host.ActivateBinding(context.Binding).IsSuccess);
            Assert.Equal(
                RuntimeIngressStatus.Queued,
                (await bundle.Host.EnqueueAsync(context.Cut(1, 1, 42))).Value.Status);

            var processed = await bundle.Host.ProcessNextDeliveryAsync();

            Assert.True(processed.IsSuccess);
            Assert.Equal(RuntimeProcessNextStatus.Published, processed.Value.Status);
            Assert.Equal((ulong)1, processed.Value.Obligation!.Position.Value);
            Assert.Equal(
                RuntimePublicationCommitStatus.Published,
                processed.Value.Publication!.Status);
            Assert.Equal((ulong)1, processed.Value.Publication.CurrentCursor.Value);
            var idle = await bundle.Host.ProcessNextAsync();
            Assert.True(idle.IsSuccess);
            Assert.False(idle.Value);
        }

        var state = await context.ReadPublishedStateAsync();
        Assert.Equal(1L, state.CompletedObligationPosition);
        Assert.Equal(1L, state.CurrentPosition);
        Assert.Equal(42L, state.CurrentValue);
        Assert.Equal(1L, state.PublishedDeliveries);
        Assert.Equal(0L, state.PendingDeliveries);
    }

    [Fact]
    public async Task RestartCompletesDeliveryAfterCheckpointCommitCrash()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var crashing = context.CreateHost(
            RuntimeDeliveryCommitPoint.PendingDeliveryPersisted);
        using (crashing.Host)
        {
            Assert.True((await crashing.Host.StartAsync()).IsSuccess);
            Assert.True(crashing.Host.ActivateBinding(context.Binding).IsSuccess);
            Assert.Equal(
                RuntimeIngressStatus.Queued,
                (await crashing.Host.EnqueueAsync(context.Cut(1, 1, 25))).Value.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => crashing.Host.ProcessNextDeliveryAsync());
            Assert.Equal(RuntimeHostState.Faulted, crashing.Host.GetReadiness().State);
            Assert.True((await crashing.Host.DrainAsync()).IsSuccess);
            Assert.Equal(RuntimeHostState.Stopped, crashing.Host.GetReadiness().State);
        }

        Assert.Equal(1L, (await context.ReadPublishedStateAsync()).PendingDeliveries);

        var recovered = context.CreateHost();
        using (recovered.Host)
        {
            Assert.True((await recovered.Host.StartAsync()).IsSuccess);
            Assert.True(recovered.Host.GetReadiness().RecoveryComplete);
            Assert.True(recovered.Host.GetReadiness().AdmissionOpen);
            Assert.Equal(
                25L,
                Assert.Single(recovered.Runtime.GetSnapshot().Entries).Value.Value);
        }

        var state = await context.ReadPublishedStateAsync();
        Assert.Equal(25L, state.CurrentValue);
        Assert.Equal(1L, state.PublishedDeliveries);
        Assert.Equal(0L, state.PendingDeliveries);
        Assert.Equal(1L, state.DeltaCount);
    }

    [Fact]
    public async Task RestartPublishesAfterDownstreamCompletionCrash()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var crashing = context.CreateHost(
            RuntimeDeliveryCommitPoint.DownstreamCompleted);
        using (crashing.Host)
        {
            Assert.True((await crashing.Host.StartAsync()).IsSuccess);
            Assert.True(crashing.Host.ActivateBinding(context.Binding).IsSuccess);
            Assert.Equal(
                RuntimeIngressStatus.Queued,
                (await crashing.Host.EnqueueAsync(context.Cut(1, 1, 30))).Value.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => crashing.Host.ProcessNextDeliveryAsync());
        }

        var pending = await context.Store.LoadPendingDeliveryAsync(context.ScopeId);
        Assert.NotNull(pending);
        Assert.Equal(
            RuntimeProcessingDeliveryStage.DownstreamCompleted,
            pending.Stage);

        var recovered = context.CreateHost();
        using (recovered.Host)
        {
            Assert.True((await recovered.Host.StartAsync()).IsSuccess);
            Assert.True(recovered.Host.GetReadiness().AdmissionOpen);
        }

        var state = await context.ReadPublishedStateAsync();
        Assert.Equal(30L, state.CurrentValue);
        Assert.Equal(1L, state.PublishedDeliveries);
        Assert.Equal(0L, state.PendingDeliveries);
        Assert.Equal(1L, state.DeltaCount);
    }

    [Fact]
    public async Task RestartAfterPublicationCommitDoesNotDuplicatePublishedResult()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var crashing = context.CreateHost(RuntimeDeliveryCommitPoint.Published);
        using (crashing.Host)
        {
            Assert.True((await crashing.Host.StartAsync()).IsSuccess);
            Assert.True(crashing.Host.ActivateBinding(context.Binding).IsSuccess);
            Assert.Equal(
                RuntimeIngressStatus.Queued,
                (await crashing.Host.EnqueueAsync(context.Cut(1, 1, 35))).Value.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => crashing.Host.ProcessNextDeliveryAsync());
            Assert.Equal(RuntimeHostState.Faulted, crashing.Host.GetReadiness().State);
        }

        var committed = await context.ReadPublishedStateAsync();
        Assert.Equal(1L, committed.CompletedObligationPosition);
        Assert.Equal(1L, committed.CurrentPosition);
        Assert.Equal(35L, committed.CurrentValue);
        Assert.Equal(1L, committed.PublishedDeliveries);
        Assert.Equal(0L, committed.PendingDeliveries);
        Assert.Equal(1L, committed.DeltaCount);
        Assert.Null(await context.Store.LoadPendingDeliveryAsync(context.ScopeId));

        var recovered = context.CreateHost();
        using (recovered.Host)
        {
            Assert.True((await recovered.Host.StartAsync()).IsSuccess);
            Assert.True(recovered.Host.GetReadiness().RecoveryComplete);
            Assert.True(recovered.Host.GetReadiness().AdmissionOpen);
            Assert.Equal(
                35L,
                Assert.Single(recovered.Runtime.GetSnapshot().Entries).Value.Value);
        }

        var afterRestart = await context.ReadPublishedStateAsync();
        Assert.Equal(committed, afterRestart);
    }

    private sealed class RuntimeTestContext : IAsyncDisposable
    {
        private static readonly SourceId SourceId = Dispatcher.Core.SourceId.From(
            Guid.Parse("c3000000-0000-0000-0000-000000000001"));
        private static readonly PointId PointId = Dispatcher.Semantics.PointId.From(
            Guid.Parse("c4000000-0000-0000-0000-000000000001"));

        private RuntimeTestContext(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            FixedClock clock)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ScopeId = RuntimeScopeId.From(
                Guid.Parse("c5000000-0000-0000-0000-000000000001"));
            Binding = new SourceBinding(
                ScopeId,
                SourceId,
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1));
            Store = new CoreRuntimeStore(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock);
        }

        public TestDatabase Database { get; }

        public NpgsqlDataSource DataSource { get; }

        public FixedClock Clock { get; }

        public RuntimeScopeId ScopeId { get; }

        public SourceBinding Binding { get; }

        public CoreRuntimeStore Store { get; }

        public static async Task<RuntimeTestContext> CreateAsync(
            PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                CoreRuntimeMigrations.CreatePlan(
                    PostgreSqlClusterFixture.OwnerBRole));
            return new RuntimeTestContext(
                database,
                dataSource,
                new FixedClock(Start));
        }

        public HostBundle CreateHost(
            RuntimeDeliveryCommitPoint? crashPoint = null)
        {
            var runtime = new CoreRuntime(
                ScopeId,
                Clock,
                Clock,
                new RuntimeCurrentLimits(
                    maxPoints: 8,
                    retainedChangeCapacity: 4));
            RuntimeDeliveryCommitHook? hook = crashPoint is null
                ? null
                : new RuntimeDeliveryCommitHook(
                    (point, _, _) =>
                    {
                        if (point == crashPoint.Value)
                        {
                            throw new InvalidOperationException(
                                $"simulated crash after {point}");
                        }

                        return Task.CompletedTask;
                    });
            return new HostBundle(
                runtime,
                new CoreRuntimeHost(
                    ScopeId,
                    runtime,
                    Store,
                    new RuntimeIngressLimits(capacity: 4),
                    deliveryCommitHook: hook));
        }

        public RuntimeCut Cut(
            ulong scheduleSequence,
            ulong sourcePosition,
            long value)
        {
            var observation = new SourceObservation(
                ScopeId,
                SourceId,
                PointId,
                new OwnerPosition<SourceObservation>(sourcePosition),
                TypedValue.From((decimal)value),
                Unit.FromSymbol("kW"),
                DataQuality.Good,
                Freshness.Fresh,
                SourceTimestamp.FromUtc(
                    Start.AddSeconds(checked((long)sourcePosition))));
            return RuntimeCut.Normalize(
                Binding,
                scheduleSequence,
                [observation]).Value;
        }

        public async Task<PublishedState> ReadPublishedStateAsync()
        {
            await using var connection = await DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var setRole = new NpgsqlCommand(
                $"SET LOCAL ROLE \"{PostgreSqlClusterFixture.OwnerBRole}\";",
                connection,
                transaction);
            await setRole.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(
                $"""
                SELECT
                    COALESCE(scope.completed_obligation_position, 0),
                    COALESCE(scope.current_position, 0),
                    COALESCE(current.value, 0),
                    (SELECT count(*)
                     FROM {CoreRuntimeMigrations.Schema}.processing_delivery
                     WHERE scope_id = @scope_id AND stage = 3),
                    (SELECT count(*)
                     FROM {CoreRuntimeMigrations.Schema}.processing_delivery
                     WHERE scope_id = @scope_id AND stage < 3),
                    (SELECT count(*)
                     FROM {CoreRuntimeMigrations.Schema}.published_delta
                     WHERE scope_id = @scope_id)
                FROM (SELECT @scope_id::uuid AS scope_id) AS requested
                LEFT JOIN {CoreRuntimeMigrations.Schema}.published_scope AS scope
                    ON scope.scope_id = requested.scope_id
                LEFT JOIN {CoreRuntimeMigrations.Schema}.published_current AS current
                    ON current.scope_id = requested.scope_id;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("scope_id", ScopeId.Value);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return new PublishedState(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5));
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private sealed record HostBundle(
        CoreRuntime Runtime,
        CoreRuntimeHost Host);

    private sealed record PublishedState(
        long CompletedObligationPosition,
        long CurrentPosition,
        long CurrentValue,
        long PublishedDeliveries,
        long PendingDeliveries,
        long DeltaCount);

    private sealed class FixedClock : IWallClock, IMonotonicClock
    {
        private readonly DateTimeOffset now;

        public FixedClock(DateTimeOffset now)
        {
            this.now = now;
        }

        public DateTimeOffset GetUtcNow() => now;

        public MonotonicTimestamp GetTimestamp() => new(0);

        public TimeSpan GetElapsedTime(
            MonotonicTimestamp start,
            MonotonicTimestamp finish) =>
            TimeSpan.FromTicks(checked(finish.Value - start.Value));
    }
}
