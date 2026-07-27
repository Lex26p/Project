using Dispatcher.Core;
using Dispatcher.History;
using Dispatcher.Persistence;
using Dispatcher.RuntimeHost;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class RuntimeHistoryDeliveryProcessorTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 27, 17, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public RuntimeHistoryDeliveryProcessorTests(
        PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task CutAcceptanceMarksOnlyHistoryStageCompleted()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var pending = await context.CreatePendingCutAsync(value: 42);
        var processor = context.CreateProcessor();

        var processed = await processor.ProcessAsync(pending);

        Assert.True(processed.IsSuccess);
        Assert.Equal(
            RuntimeHistoryDeliveryStatus.Accepted,
            processed.Value.Status);
        var sample = Assert.Single(processed.Value.Acceptance!.Samples);
        Assert.Equal(42L, sample.Value.Value);

        var progress = await context.CoreStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(progress);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            progress.HistoryState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            progress.AlarmState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            progress.EventState);
        Assert.Null(progress.LastErrorCode);

        var replay = await context.CreateProcessor().ProcessAsync(progress);
        Assert.True(replay.IsSuccess);
        Assert.Equal(
            RuntimeHistoryDeliveryStatus.AlreadyCompleted,
            replay.Value.Status);
        Assert.Null(replay.Value.Acceptance);

        var stored = await context.HistoryStore.AcceptAsync(
            pending.Obligation);
        Assert.Equal(HistoryIngestStatus.Duplicate, stored.Value.Status);
        Assert.Single(stored.Value.Samples);
    }

    [Fact]
    public async Task CrashAfterHistoryCommitReplaysDuplicateAndCompletesStage()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var pending = await context.CreatePendingCutAsync(value: 25);
        var crashing = context.CreateProcessor(
            new HistoryIngestCommitHook(
                (_, _) => throw new InvalidOperationException(
                    "simulated crash after History commit")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => crashing.ProcessAsync(pending));

        var afterCrash = await context.CoreStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(afterCrash);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            afterCrash.HistoryState);
        Assert.Null(afterCrash.LastErrorCode);

        var replay = await context.CreateProcessor().ProcessAsync(afterCrash);
        Assert.True(replay.IsSuccess);
        Assert.Equal(
            RuntimeHistoryDeliveryStatus.Duplicate,
            replay.Value.Status);
        Assert.Equal(
            HistoryIngestStatus.Duplicate,
            replay.Value.Acceptance!.Status);
        Assert.Single(replay.Value.Acceptance.Samples);

        var recovered = await context.CoreStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(recovered);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            recovered.HistoryState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            recovered.AlarmState);
    }

    [Fact]
    public async Task GapAcceptanceCreatesHistoryGapWithoutSamples()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var pending = await context.CreatePendingGapAsync();
        var processor = context.CreateProcessor();

        var processed = await processor.ProcessAsync(pending);

        Assert.True(processed.IsSuccess);
        Assert.Equal(
            RuntimeHistoryDeliveryStatus.Accepted,
            processed.Value.Status);
        Assert.Empty(processed.Value.Acceptance!.Samples);
        Assert.Equal(
            "source_evidence_unavailable",
            processed.Value.Acceptance.Gap!.Reason);

        var progress = await context.CoreStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(progress);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            progress.HistoryState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            progress.AlarmState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            progress.EventState);
    }

    [Fact]
    public async Task HistoryConflictPersistsSafeFailureAndLeavesStagePending()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var pending = await context.CreatePendingCutAsync(value: 30);
        var conflicting = context.CreateHistoryObligation(value: 99);
        Assert.True((await context.HistoryStore.AcceptAsync(
            conflicting)).IsSuccess);

        var processed = await context.CreateProcessor().ProcessAsync(pending);

        Assert.Equal(
            "history.ingest_conflict",
            processed.Error?.Code.Value);
        var failed = await context.CoreStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(failed);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            failed.HistoryState);
        Assert.Equal("history.ingest_conflict", failed.LastErrorCode);
        Assert.Equal(Start, failed.LastErrorAt);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            failed.AlarmState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            failed.EventState);
    }

    private sealed class RuntimeTestContext : IAsyncDisposable
    {
        private static readonly SourceId SourceId = Dispatcher.Core.SourceId.From(
            Guid.Parse("cf000000-0000-0000-0000-000000000001"));
        private static readonly PointId PointId = Dispatcher.Semantics.PointId.From(
            Guid.Parse("d0000000-0000-0000-0000-000000000001"));

        private RuntimeTestContext(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            FixedClock clock)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ScopeId = RuntimeScopeId.From(
                Guid.Parse("d1000000-0000-0000-0000-000000000001"));
            Binding = new SourceBinding(
                ScopeId,
                SourceId,
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1));
            CoreStore = new CoreRuntimeStore(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock);
            HistoryStore = CreateHistoryStore();
        }

        public TestDatabase Database { get; }

        public NpgsqlDataSource DataSource { get; }

        public FixedClock Clock { get; }

        public RuntimeScopeId ScopeId { get; }

        public SourceBinding Binding { get; }

        public CoreRuntimeStore CoreStore { get; }

        public HistoryStore HistoryStore { get; }

        public static async Task<RuntimeTestContext> CreateAsync(
            PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                CoreRuntimeMigrations.CreatePlan(
                    PostgreSqlClusterFixture.OwnerBRole));
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                HistoryMigrations.CreatePlan(
                    PostgreSqlClusterFixture.OwnerBRole));
            return new RuntimeTestContext(
                database,
                dataSource,
                new FixedClock(Start));
        }

        public RuntimeHistoryDeliveryProcessor CreateProcessor(
            HistoryIngestCommitHook? hook = null) =>
            new(
                CreateHistoryStore(hook),
                CoreStore);

        public HistoryStore CreateHistoryStore(
            HistoryIngestCommitHook? hook = null) =>
            new(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock,
                new HistoryQueryLimits(
                    maxPageSize: 100,
                    maxAggregateBuckets: 100),
                hook);

        public async Task<RuntimeProcessingDelivery> CreatePendingCutAsync(
            long value)
        {
            var runtime = CreateRuntime();
            Assert.True(runtime.ActivateBinding(Binding).IsSuccess);
            var cut = CreateCut(value);
            var obligation = await CoreStore.AppendCutAsync(cut);
            var acceptance = runtime.Apply(cut).Value;
            var saved = await CoreStore.SaveCheckpointWithPendingDeliveryAsync(
                runtime.CaptureCheckpoint(),
                obligation,
                acceptance,
                protectedContinuity: true);
            Assert.True(saved.IsSuccess);
            return saved.Value;
        }

        public async Task<RuntimeProcessingDelivery> CreatePendingGapAsync()
        {
            var runtime = CreateRuntime();
            Assert.True(runtime.ActivateBinding(Binding).IsSuccess);
            var gap = new RuntimeSourceGap(
                Binding,
                scheduleSequence: 1,
                firstSourcePosition: 1,
                lastSourcePosition: 3,
                reason: "source_evidence_unavailable");
            var obligation = await CoreStore.AppendGapAsync(gap);
            Assert.True(runtime.ApplyGap(gap).IsSuccess);
            var saved = await CoreStore.SaveCheckpointWithPendingDeliveryAsync(
                runtime.CaptureCheckpoint(),
                obligation,
                postCutAcceptance: null,
                protectedContinuity: false);
            Assert.True(saved.IsSuccess);
            return saved.Value;
        }

        public RuntimeSourceObligation CreateHistoryObligation(long value)
        {
            var cut = CreateCut(value);
            return new RuntimeSourceObligation(
                ScopeId,
                new OwnerPosition<RuntimeSourceObligation>(1),
                RuntimeFactClass.SourceCut,
                RuntimeFactProtection.Protected,
                Binding,
                1,
                cut,
                null,
                Start,
                null);
        }

        private CoreRuntime CreateRuntime() =>
            new(
                ScopeId,
                Clock,
                Clock,
                new RuntimeCurrentLimits(
                    maxPoints: 8,
                    retainedChangeCapacity: 16));

        private RuntimeCut CreateCut(long value) =>
            RuntimeCut.Normalize(
                Binding,
                scheduleSequence: 1,
                [
                    new SourceObservation(
                        ScopeId,
                        SourceId,
                        PointId,
                        new OwnerPosition<SourceObservation>(1),
                        TypedValue.From(value),
                        Unit.FromSymbol("kW"),
                        DataQuality.Good,
                        Freshness.Fresh,
                        SourceTimestamp.FromUtc(Start.AddSeconds(1))),
                ]).Value;

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
