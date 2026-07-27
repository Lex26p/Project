using Dispatcher.Core;
using Dispatcher.Persistence;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class CoreRuntimePublishedReadTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public CoreRuntimePublishedReadTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task SnapshotAndDeltaUseConsistentBoundedPublishedState()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var runtime = context.CreateRuntime();
        Assert.True(runtime.ActivateBinding(context.Binding).IsSuccess);

        await context.PublishAsync(runtime, 1, 1, 10, retainedDeltaCapacity: 2);
        await context.PublishAsync(runtime, 2, 2, 20, retainedDeltaCapacity: 2);
        await context.PublishAsync(runtime, 3, 3, 30, retainedDeltaCapacity: 2);

        var readiness = await context.Reader.ReadReadinessAsync(context.ScopeId);
        Assert.True(readiness.Published);
        Assert.True(readiness.Ready);
        Assert.True(readiness.ProtectedContinuity);
        Assert.True(readiness.CanServeCurrent);
        Assert.Equal((ulong)3, readiness.CompletedObligationPosition.Value);
        Assert.Equal((ulong)3, readiness.CurrentCursor.Value);
        Assert.Equal((ulong)1, readiness.EarliestResumableCursor.Value);

        var snapshot = await context.Reader.ReadSnapshotAsync(context.ScopeId);
        Assert.Equal(readiness.CurrentCursor, snapshot.Cursor);
        var current = Assert.Single(snapshot.Entries);
        Assert.Equal(30L, current.Value.Value);
        Assert.Equal((ulong)3, current.SourcePosition.Value);
        Assert.Equal((ulong)3, current.CurrentPosition.Value);

        var available = await context.Reader.ReadDeltaAsync(
            context.ScopeId,
            new ConsumerCursor<PublishedCurrentEntry>(1));
        Assert.Equal(PublishedCurrentDeltaStatus.Available, available.Status);
        Assert.False(available.RequiresSnapshot);
        Assert.Equal((ulong)3, available.To.Value);
        Assert.Equal([2UL, 3UL], available.Changes
            .Select(item => item.CurrentPosition.Value)
            .ToArray());
        Assert.Equal([20L, 30L], available.Changes
            .Select(item => item.Value.Value)
            .ToArray());

        var tooOld = await context.Reader.ReadDeltaAsync(
            context.ScopeId,
            new ConsumerCursor<PublishedCurrentEntry>(0));
        Assert.Equal(PublishedCurrentDeltaStatus.CursorTooOld, tooOld.Status);
        Assert.True(tooOld.RequiresSnapshot);
        Assert.Empty(tooOld.Changes);
        Assert.Equal((ulong)1, tooOld.Readiness.EarliestResumableCursor.Value);

        var ahead = await context.Reader.ReadDeltaAsync(
            context.ScopeId,
            new ConsumerCursor<PublishedCurrentEntry>(4));
        Assert.Equal(PublishedCurrentDeltaStatus.CursorAhead, ahead.Status);
        Assert.False(ahead.RequiresSnapshot);
        Assert.Empty(ahead.Changes);

        var atCurrent = await context.Reader.ReadDeltaAsync(
            context.ScopeId,
            new ConsumerCursor<PublishedCurrentEntry>(3));
        Assert.Equal(PublishedCurrentDeltaStatus.Available, atCurrent.Status);
        Assert.Empty(atCurrent.Changes);

        var counts = await context.ReadStorageCountsAsync();
        Assert.Equal(1L, counts.CurrentCount);
        Assert.Equal(2L, counts.DeltaCount);
    }

    [Fact]
    public async Task MissingPublishedScopeReturnsExplicitReadState()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var readiness = await context.Reader.ReadReadinessAsync(context.ScopeId);
        Assert.False(readiness.Published);
        Assert.False(readiness.CanServeCurrent);
        Assert.Equal((ulong)0, readiness.CurrentCursor.Value);

        var snapshot = await context.Reader.ReadSnapshotAsync(context.ScopeId);
        Assert.False(snapshot.Readiness.Published);
        Assert.Empty(snapshot.Entries);

        var delta = await context.Reader.ReadDeltaAsync(
            context.ScopeId,
            new ConsumerCursor<PublishedCurrentEntry>(0));
        Assert.Equal(
            PublishedCurrentDeltaStatus.ScopeNotPublished,
            delta.Status);
        Assert.True(delta.RequiresSnapshot);
        Assert.Empty(delta.Changes);
    }

    [Fact]
    public async Task ConfiguredReadLimitsRejectOversizedSnapshotAndPageDeltaWithoutSkipping()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var runtime = context.CreateRuntime();
        Assert.True(runtime.ActivateBinding(context.Binding).IsSuccess);
        var secondPoint = Dispatcher.Semantics.PointId.From(
            Guid.Parse("c7000000-0000-0000-0000-000000000002"));
        await context.PublishAsync(
            runtime, 1, 1, 10, retainedDeltaCapacity: 8);
        await context.PublishAsync(
            runtime, 2, 2, 20, retainedDeltaCapacity: 8, secondPoint);
        var bounded = new CoreRuntimePublishedReader(
            context.DataSource,
            PostgreSqlClusterFixture.OwnerBRole,
            new PublishedCurrentReadLimits(1, 1));

        await Assert.ThrowsAsync<PublishedCurrentReadLimitExceededException>(
            () => bounded.ReadSnapshotAsync(context.ScopeId));
        var first = await bounded.ReadDeltaAsync(
            context.ScopeId,
            new ConsumerCursor<PublishedCurrentEntry>(0));
        Assert.Single(first.Changes);
        Assert.Equal((ulong)1, first.To.Value);
        var second = await bounded.ReadDeltaAsync(context.ScopeId, first.To);
        Assert.Single(second.Changes);
        Assert.Equal((ulong)2, second.To.Value);
    }

    private sealed class RuntimeTestContext : IAsyncDisposable
    {
        private static readonly SourceId SourceId = Dispatcher.Core.SourceId.From(
            Guid.Parse("c6000000-0000-0000-0000-000000000001"));
        private static readonly PointId PointId = Dispatcher.Semantics.PointId.From(
            Guid.Parse("c7000000-0000-0000-0000-000000000001"));

        private RuntimeTestContext(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            FixedClock clock)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ScopeId = RuntimeScopeId.From(
                Guid.Parse("c8000000-0000-0000-0000-000000000001"));
            Binding = new SourceBinding(
                ScopeId,
                SourceId,
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1));
            Store = new CoreRuntimeStore(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock);
            Reader = new CoreRuntimePublishedReader(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                new PublishedCurrentReadLimits(64, 64));
        }

        public TestDatabase Database { get; }

        public NpgsqlDataSource DataSource { get; }

        public FixedClock Clock { get; }

        public RuntimeScopeId ScopeId { get; }

        public SourceBinding Binding { get; }

        public CoreRuntimeStore Store { get; }

        public CoreRuntimePublishedReader Reader { get; }

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
                retainedChangeCapacity: 8));

        public async Task PublishAsync(
            CoreRuntime runtime,
            ulong scheduleSequence,
            ulong sourcePosition,
            long value,
            int retainedDeltaCapacity,
            PointId? targetPointId = null)
        {
            var cut = Cut(
                scheduleSequence,
                sourcePosition,
                value,
                targetPointId ?? PointId);
            var obligation = await Store.AppendCutAsync(cut);
            var acceptance = runtime.Apply(cut).Value;
            var saved = await Store.SaveCheckpointWithPendingDeliveryAsync(
                runtime.CaptureCheckpoint(),
                obligation,
                acceptance,
                protectedContinuity: true);
            Assert.True(saved.IsSuccess);
            Assert.True((await Store.CompleteDownstreamAsync(
                ScopeId,
                obligation.Position)).IsSuccess);
            Assert.True((await Store.PublishCompletedDeliveryAsync(
                ScopeId,
                obligation.Position,
                retainedDeltaCapacity,
                ready: true)).IsSuccess);
        }

        public RuntimeCut Cut(
            ulong scheduleSequence,
            ulong sourcePosition,
            long value,
            PointId targetPointId)
        {
            var observation = new SourceObservation(
                ScopeId,
                SourceId,
                targetPointId,
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

        public async Task<StorageCounts> ReadStorageCountsAsync()
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
                    (SELECT count(*)
                     FROM {CoreRuntimeMigrations.Schema}.published_current
                     WHERE scope_id = @scope_id),
                    (SELECT count(*)
                     FROM {CoreRuntimeMigrations.Schema}.published_delta
                     WHERE scope_id = @scope_id);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("scope_id", ScopeId.Value);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return new StorageCounts(
                reader.GetInt64(0),
                reader.GetInt64(1));
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private sealed record StorageCounts(
        long CurrentCount,
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
