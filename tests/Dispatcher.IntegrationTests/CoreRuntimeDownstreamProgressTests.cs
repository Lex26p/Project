using Dispatcher.Core;
using Dispatcher.Persistence;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class CoreRuntimeDownstreamProgressTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 27, 16, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public CoreRuntimeDownstreamProgressTests(
        PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task DownstreamStagesCommitInOrderAndReplayIsIdempotent()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var pending = await context.CreatePendingDeliveryAsync();

        var outOfOrder = await context.Store.CompleteDownstreamStageAsync(
            context.ScopeId,
            pending.ObligationPosition,
            RuntimeDownstreamStage.Alarm);
        Assert.Equal("runtime.delivery_stage_order", outOfOrder.Error?.Code.Value);

        var history = await context.Store.CompleteDownstreamStageAsync(
            context.ScopeId,
            pending.ObligationPosition,
            RuntimeDownstreamStage.History);
        Assert.True(history.IsSuccess);
        Assert.Equal(
            RuntimeDownstreamStageCommitStatus.Completed,
            history.Value.Status);

        var duplicateHistory = await context.Store.CompleteDownstreamStageAsync(
            context.ScopeId,
            pending.ObligationPosition,
            RuntimeDownstreamStage.History);
        Assert.True(duplicateHistory.IsSuccess);
        Assert.Equal(
            RuntimeDownstreamStageCommitStatus.AlreadyCompleted,
            duplicateHistory.Value.Status);

        var afterHistory = await context.Store.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(afterHistory);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            afterHistory.HistoryState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            afterHistory.AlarmState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            afterHistory.EventState);

        Assert.True((await context.Store.CompleteDownstreamStageAsync(
            context.ScopeId,
            pending.ObligationPosition,
            RuntimeDownstreamStage.Alarm)).IsSuccess);
        Assert.True((await context.Store.CompleteDownstreamStageAsync(
            context.ScopeId,
            pending.ObligationPosition,
            RuntimeDownstreamStage.Event)).IsSuccess);

        var completedStages = await context.Store.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(completedStages);
        Assert.Equal(
            RuntimeProcessingDeliveryStage.PendingDownstream,
            completedStages.Stage);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            completedStages.HistoryState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            completedStages.AlarmState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            completedStages.EventState);

        Assert.True((await context.Store.CompleteDownstreamAsync(
            context.ScopeId,
            pending.ObligationPosition)).IsSuccess);
        var downstreamCompleted = await context.Store.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(downstreamCompleted);
        Assert.Equal(
            RuntimeProcessingDeliveryStage.DownstreamCompleted,
            downstreamCompleted.Stage);
    }

    [Fact]
    public async Task FailureCodePersistsAcrossStoreRestartUntilStageCompletes()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var pending = await context.CreatePendingDeliveryAsync();

        var recorded = await context.Store.RecordDownstreamFailureAsync(
            context.ScopeId,
            pending.ObligationPosition,
            RuntimeDownstreamStage.History,
            "history.accept_unavailable");
        Assert.True(recorded.IsSuccess);

        var restartedStore = context.CreateStore();
        var failed = await restartedStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(failed);
        Assert.Equal("history.accept_unavailable", failed.LastErrorCode);
        Assert.Equal(Start, failed.LastErrorAt);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            failed.HistoryState);

        var wrongStage = await restartedStore.RecordDownstreamFailureAsync(
            context.ScopeId,
            pending.ObligationPosition,
            RuntimeDownstreamStage.Event,
            "event.unavailable");
        Assert.Equal(
            "runtime.delivery_stage_order",
            wrongStage.Error?.Code.Value);

        var history = await restartedStore.CompleteDownstreamStageAsync(
            context.ScopeId,
            pending.ObligationPosition,
            RuntimeDownstreamStage.History);
        Assert.True(history.IsSuccess);

        var recovered = await restartedStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(recovered);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            recovered.HistoryState);
        Assert.Null(recovered.LastErrorCode);
        Assert.Null(recovered.LastErrorAt);

        var completedFailure = await restartedStore.RecordDownstreamFailureAsync(
            context.ScopeId,
            pending.ObligationPosition,
            RuntimeDownstreamStage.History,
            "history.should_not_replace");
        Assert.Equal(
            "runtime.delivery_stage_completed",
            completedFailure.Error?.Code.Value);
    }

    private sealed class RuntimeTestContext : IAsyncDisposable
    {
        private static readonly SourceId SourceId = Dispatcher.Core.SourceId.From(
            Guid.Parse("cc000000-0000-0000-0000-000000000001"));
        private static readonly PointId PointId = Dispatcher.Semantics.PointId.From(
            Guid.Parse("cd000000-0000-0000-0000-000000000001"));

        private RuntimeTestContext(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            FixedClock clock)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ScopeId = RuntimeScopeId.From(
                Guid.Parse("ce000000-0000-0000-0000-000000000001"));
            Binding = new SourceBinding(
                ScopeId,
                SourceId,
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1));
            Store = CreateStore();
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

        public CoreRuntimeStore CreateStore() => new(
            DataSource,
            PostgreSqlClusterFixture.OwnerBRole,
            Clock);

        public async Task<RuntimeProcessingDelivery> CreatePendingDeliveryAsync()
        {
            var runtime = new CoreRuntime(
                ScopeId,
                Clock,
                Clock,
                new RuntimeCurrentLimits(
                    maxPoints: 8,
                    retainedChangeCapacity: 16));
            Assert.True(runtime.ActivateBinding(Binding).IsSuccess);
            var cut = RuntimeCut.Normalize(
                Binding,
                scheduleSequence: 1,
                [
                    new SourceObservation(
                        ScopeId,
                        SourceId,
                        PointId,
                        new OwnerPosition<SourceObservation>(1),
                        TypedValue.From(42m),
                        Unit.FromSymbol("kW"),
                        DataQuality.Good,
                        Freshness.Fresh,
                        SourceTimestamp.FromUtc(Start.AddSeconds(1))),
                ]).Value;
            var obligation = await Store.AppendCutAsync(cut);
            var acceptance = runtime.Apply(cut).Value;
            var saved = await Store.SaveCheckpointWithPendingDeliveryAsync(
                runtime.CaptureCheckpoint(),
                obligation,
                acceptance,
                protectedContinuity: true);
            Assert.True(saved.IsSuccess);
            return saved.Value;
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
