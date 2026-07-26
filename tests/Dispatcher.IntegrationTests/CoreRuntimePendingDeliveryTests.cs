using Dispatcher.Core;
using Dispatcher.Persistence;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class CoreRuntimePendingDeliveryTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 27, 8, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public CoreRuntimePendingDeliveryTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task CheckpointAndPendingDeliveryCommitAtomically()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var runtime = context.CreateRuntime();
        Assert.True(runtime.ActivateBinding(context.Binding).IsSuccess);
        var cut = context.Cut(scheduleSequence: 1, sourcePosition: 1, value: 42);
        var obligation = await context.Store.AppendCutAsync(cut);
        var acceptance = runtime.Apply(cut).Value;

        var saved = await context.Store.SaveCheckpointWithPendingDeliveryAsync(
            runtime.CaptureCheckpoint(),
            obligation,
            acceptance,
            protectedContinuity: true);

        Assert.True(saved.IsSuccess);
        Assert.Equal(RuntimeProcessingDeliveryStage.PendingDownstream, saved.Value.Stage);
        Assert.Equal(obligation.Position, saved.Value.ObligationPosition);
        Assert.Equal(42L, Assert.Single(saved.Value.PostCutAcceptance!.CurrentTransitions).Value.Value);

        var pending = await context.Store.LoadPendingDeliveryAsync(context.ScopeId);
        Assert.NotNull(pending);
        Assert.Equal(obligation.Position, pending.ObligationPosition);
        Assert.Equal(RuntimeFactClass.SourceCut, pending.Obligation.FactClass);
        Assert.Equal(42L, Assert.Single(pending.PostCutAcceptance!.CurrentTransitions).Value.Value);
        Assert.Equal(RuntimeDownstreamDeliveryState.Pending, pending.HistoryState);
        Assert.Equal(RuntimeDownstreamDeliveryState.Pending, pending.AlarmState);
        Assert.Equal(RuntimeDownstreamDeliveryState.Pending, pending.EventState);

        var recovery = await context.Store.LoadRecoveryAsync(context.ScopeId);
        Assert.NotNull(recovery.Checkpoint);
        Assert.Equal((ulong)1, recovery.CheckpointObligationPosition);
        Assert.Empty(recovery.PendingObligations);
        Assert.Equal(0L, await context.Store.CountPendingAsync(context.ScopeId));
    }

    [Fact]
    public async Task PendingDeliveryPreventsNextCheckpointFromOvertaking()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var runtime = context.CreateRuntime();
        Assert.True(runtime.ActivateBinding(context.Binding).IsSuccess);

        var firstCut = context.Cut(scheduleSequence: 1, sourcePosition: 1, value: 10);
        var firstObligation = await context.Store.AppendCutAsync(firstCut);
        var firstAcceptance = runtime.Apply(firstCut).Value;
        Assert.True((await context.Store.SaveCheckpointWithPendingDeliveryAsync(
            runtime.CaptureCheckpoint(),
            firstObligation,
            firstAcceptance,
            protectedContinuity: true)).IsSuccess);

        var secondCut = context.Cut(scheduleSequence: 2, sourcePosition: 2, value: 20);
        var secondObligation = await context.Store.AppendCutAsync(secondCut);
        var secondAcceptance = runtime.Apply(secondCut).Value;
        var blocked = await context.Store.SaveCheckpointWithPendingDeliveryAsync(
            runtime.CaptureCheckpoint(),
            secondObligation,
            secondAcceptance,
            protectedContinuity: true);

        Assert.Equal("runtime.delivery_pending", blocked.Error?.Code.Value);
        var pending = await context.Store.LoadPendingDeliveryAsync(context.ScopeId);
        Assert.NotNull(pending);
        Assert.Equal(firstObligation.Position, pending.ObligationPosition);

        var recovery = await context.Store.LoadRecoveryAsync(context.ScopeId);
        Assert.Equal((ulong)1, recovery.CheckpointObligationPosition);
        var replay = Assert.Single(recovery.PendingObligations);
        Assert.Equal(secondObligation.Position, replay.Position);
        Assert.Equal(1L, await context.Store.CountPendingAsync(context.ScopeId));
    }

    private sealed class RuntimeTestContext : IAsyncDisposable
    {
        private static readonly SourceId SourceId = Dispatcher.Core.SourceId.From(
            Guid.Parse("bd000000-0000-0000-0000-000000000001"));
        private static readonly PointId PointId = Dispatcher.Semantics.PointId.From(
            Guid.Parse("be000000-0000-0000-0000-000000000001"));

        private RuntimeTestContext(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            FixedClock clock)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ScopeId = RuntimeScopeId.From(
                Guid.Parse("bf000000-0000-0000-0000-000000000001"));
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

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

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
