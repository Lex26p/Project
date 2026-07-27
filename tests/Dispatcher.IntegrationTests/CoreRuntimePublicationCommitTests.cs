using Dispatcher.Core;
using Dispatcher.Persistence;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class CoreRuntimePublicationCommitTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public CoreRuntimePublicationCommitTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task DownstreamCompletionPublishesAtomicallyAndReplayIsIdempotent()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var runtime = context.CreateRuntime();
        Assert.True(runtime.ActivateBinding(context.Binding).IsSuccess);

        var first = await context.CreatePendingDeliveryAsync(
            runtime,
            scheduleSequence: 1,
            sourcePosition: 1,
            value: 10);

        var blocked = await context.Store.PublishCompletedDeliveryAsync(
            context.ScopeId,
            first.Obligation.Position,
            retainedDeltaCapacity: 1,
            ready: true);
        Assert.Equal("runtime.delivery_downstream_pending", blocked.Error?.Code.Value);

        Assert.True((await context.Store.CompleteDownstreamAsync(
            context.ScopeId,
            first.Obligation.Position)).IsSuccess);
        var completed = await context.Store.LoadPendingDeliveryAsync(context.ScopeId);
        Assert.NotNull(completed);
        Assert.Equal(RuntimeProcessingDeliveryStage.DownstreamCompleted, completed.Stage);

        var published = await context.Store.PublishCompletedDeliveryAsync(
            context.ScopeId,
            first.Obligation.Position,
            retainedDeltaCapacity: 1,
            ready: true);
        Assert.True(published.IsSuccess);
        Assert.Equal(RuntimePublicationCommitStatus.Published, published.Value.Status);
        Assert.Equal((ulong)1, published.Value.CurrentCursor.Value);
        Assert.Equal(1, published.Value.PublishedTransitionCount);
        Assert.Equal(1, published.Value.RetainedDeltaCount);
        Assert.Null(await context.Store.LoadPendingDeliveryAsync(context.ScopeId));

        var replay = await context.Store.PublishCompletedDeliveryAsync(
            context.ScopeId,
            first.Obligation.Position,
            retainedDeltaCapacity: 1,
            ready: true);
        Assert.True(replay.IsSuccess);
        Assert.Equal(RuntimePublicationCommitStatus.AlreadyPublished, replay.Value.Status);
        Assert.Equal((ulong)1, replay.Value.CurrentCursor.Value);

        var second = await context.CreatePendingDeliveryAsync(
            runtime,
            scheduleSequence: 2,
            sourcePosition: 2,
            value: 20);
        Assert.True((await context.Store.CompleteDownstreamAsync(
            context.ScopeId,
            second.Obligation.Position)).IsSuccess);
        var secondPublished = await context.Store.PublishCompletedDeliveryAsync(
            context.ScopeId,
            second.Obligation.Position,
            retainedDeltaCapacity: 1,
            ready: true);
        Assert.True(secondPublished.IsSuccess);
        Assert.Equal((ulong)2, secondPublished.Value.CurrentCursor.Value);
        Assert.Equal(1, secondPublished.Value.RetainedDeltaCount);

        var state = await context.ReadPublishedStateAsync();
        Assert.Equal(2L, state.CompletedObligationPosition);
        Assert.Equal(2L, state.CurrentPosition);
        Assert.Equal(2L, state.EarliestDeltaPosition);
        Assert.True(state.Ready);
        Assert.Equal(1L, state.CurrentCount);
        Assert.Equal(20L, state.CurrentValue);
        Assert.Equal(1L, state.DeltaCount);
        Assert.Equal(2L, state.OnlyDeltaPosition);
        Assert.Equal(2L, state.PublishedDeliveryCount);
    }

    [Fact]
    public async Task PublicationRejectsOutOfOrderFinalization()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var runtime = context.CreateRuntime();
        Assert.True(runtime.ActivateBinding(context.Binding).IsSuccess);
        var pending = await context.CreatePendingDeliveryAsync(
            runtime,
            scheduleSequence: 1,
            sourcePosition: 1,
            value: 42);
        Assert.True((await context.Store.CompleteDownstreamAsync(
            context.ScopeId,
            pending.Obligation.Position)).IsSuccess);
        await context.InjectPublishedCursorAheadAsync();

        var rejected = await context.Store.PublishCompletedDeliveryAsync(
            context.ScopeId,
            pending.Obligation.Position,
            retainedDeltaCapacity: 4,
            ready: true);

        Assert.Equal("runtime.delivery_publish_order", rejected.Error?.Code.Value);
        var stillPending = await context.Store.LoadPendingDeliveryAsync(context.ScopeId);
        Assert.NotNull(stillPending);
        Assert.Equal(
            RuntimeProcessingDeliveryStage.DownstreamCompleted,
            stillPending.Stage);
        Assert.Equal(0L, (await context.ReadPublishedStateAsync()).CurrentCount);
    }

    private sealed class RuntimeTestContext : IAsyncDisposable
    {
        private static readonly SourceId SourceId = Dispatcher.Core.SourceId.From(
            Guid.Parse("c0000000-0000-0000-0000-000000000001"));
        private static readonly PointId PointId = Dispatcher.Semantics.PointId.From(
            Guid.Parse("c1000000-0000-0000-0000-000000000001"));

        private RuntimeTestContext(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            FixedClock clock)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ScopeId = RuntimeScopeId.From(
                Guid.Parse("c2000000-0000-0000-0000-000000000001"));
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

        public CoreRuntime CreateRuntime() => new(
            ScopeId,
            Clock,
            Clock,
            new RuntimeCurrentLimits(
                maxPoints: 8,
                retainedChangeCapacity: 16));

        public async Task<PendingDelivery> CreatePendingDeliveryAsync(
            CoreRuntime runtime,
            ulong scheduleSequence,
            ulong sourcePosition,
            long value)
        {
            var cut = Cut(scheduleSequence, sourcePosition, value);
            var obligation = await Store.AppendCutAsync(cut);
            var acceptance = runtime.Apply(cut).Value;
            var saved = await Store.SaveCheckpointWithPendingDeliveryAsync(
                runtime.CaptureCheckpoint(),
                obligation,
                acceptance,
                protectedContinuity: true);
            Assert.True(saved.IsSuccess);
            return new PendingDelivery(obligation);
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
                TypedValue.From(value),
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

        public async Task InjectPublishedCursorAheadAsync()
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
                INSERT INTO {CoreRuntimeMigrations.Schema}.published_scope
                    (scope_id, completed_obligation_position, current_position,
                     earliest_delta_position, protected_continuity, ready,
                     heartbeat_at, published_at)
                VALUES (@scope_id, 1, 0, 1, true, true, now(), now());
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("scope_id", ScopeId.Value);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
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
                    scope.completed_obligation_position,
                    scope.current_position,
                    scope.earliest_delta_position,
                    scope.ready,
                    (SELECT count(*) FROM {CoreRuntimeMigrations.Schema}.published_current
                     WHERE scope_id = @scope_id),
                    COALESCE((SELECT value FROM {CoreRuntimeMigrations.Schema}.published_current
                              WHERE scope_id = @scope_id LIMIT 1), 0),
                    (SELECT count(*) FROM {CoreRuntimeMigrations.Schema}.published_delta
                     WHERE scope_id = @scope_id),
                    COALESCE((SELECT current_position FROM {CoreRuntimeMigrations.Schema}.published_delta
                              WHERE scope_id = @scope_id LIMIT 1), 0),
                    (SELECT count(*) FROM {CoreRuntimeMigrations.Schema}.processing_delivery
                     WHERE scope_id = @scope_id AND stage = 3)
                FROM {CoreRuntimeMigrations.Schema}.published_scope AS scope
                WHERE scope.scope_id = @scope_id;
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
                reader.GetBoolean(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8));
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private sealed record PendingDelivery(RuntimeSourceObligation Obligation);

    private sealed record PublishedState(
        long CompletedObligationPosition,
        long CurrentPosition,
        long EarliestDeltaPosition,
        bool Ready,
        long CurrentCount,
        long CurrentValue,
        long DeltaCount,
        long OnlyDeltaPosition,
        long PublishedDeliveryCount);

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
