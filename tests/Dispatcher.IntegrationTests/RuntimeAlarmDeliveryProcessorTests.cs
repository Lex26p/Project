using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.History;
using Dispatcher.Persistence;
using Dispatcher.RuntimeHost;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class RuntimeAlarmDeliveryProcessorTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 27, 18, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public RuntimeAlarmDeliveryProcessorTests(
        PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task ThresholdEvaluationUsesDurablePostCutSnapshotAndCompletesAlarm()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        Assert.True((await context.ActivateDefinitionsAsync(
            RevisionNumber.Initial,
            threshold: 100)).IsSuccess);
        var pending = await context.CreatePendingCutAsync(
            value: 110,
            RevisionNumber.Initial);
        var afterHistory = await context.CompleteHistoryAsync(pending);

        var processed = await context
            .CreateAlarmProcessor()
            .ProcessAsync(afterHistory);

        Assert.True(processed.IsSuccess);
        Assert.Equal(
            RuntimeAlarmDeliveryStatus.Evaluated,
            processed.Value.Status);
        Assert.Equal(
            context.ConfigurationRevisionId,
            processed.Value.ConfigurationRevisionId!.Value);
        Assert.Equal(
            RevisionNumber.Initial,
            processed.Value.DefinitionEpoch!.Value);
        var occurrence = Assert.Single(
            processed.Value.Evaluation!.Occurrences);
        Assert.Equal(AlarmConditionState.Active, occurrence.Condition.State);
        Assert.Equal(context.PointId, occurrence.PointId);

        var progress = await context.CoreStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(progress);
        Assert.Equal(
            context.ConfigurationRevisionId,
            progress.DefinitionEpoch!.Value);
        Assert.Equal(
            RevisionNumber.Initial,
            progress.AlarmDefinitionEpoch!.Value);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            progress.HistoryState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            progress.AlarmState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            progress.EventState);
        Assert.Null(progress.LastErrorCode);
    }

    [Fact]
    public async Task CrashAfterAlarmCommitReplaysWithoutSecondOccurrence()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        Assert.True((await context.ActivateDefinitionsAsync(
            RevisionNumber.Initial,
            threshold: 100)).IsSuccess);
        var pending = await context.CreatePendingCutAsync(
            value: 120,
            RevisionNumber.Initial);
        var afterHistory = await context.CompleteHistoryAsync(pending);
        var crashing = context.CreateAlarmProcessor(
            new RuntimeAlarmEvaluationCommitHook(
                (_, _) => throw new InvalidOperationException(
                    "simulated crash after Alarm commit")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => crashing.ProcessAsync(afterHistory));

        var committedOccurrence = Assert.Single(
            await context.AlarmStore.ReadOccurrencesAsync(
                context.ScopeId));
        var afterCrash = await context.CoreStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(afterCrash);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            afterCrash.HistoryState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            afterCrash.AlarmState);
        Assert.Null(afterCrash.LastErrorCode);

        var replay = await context
            .CreateAlarmProcessor()
            .ProcessAsync(afterCrash);

        Assert.True(replay.IsSuccess);
        Assert.Equal(
            RuntimeAlarmDeliveryStatus.Evaluated,
            replay.Value.Status);
        Assert.Equal(
            committedOccurrence.OccurrenceId,
            Assert.Single(replay.Value.Evaluation!.Occurrences).OccurrenceId);
        var occurrencesAfterReplay =
            await context.AlarmStore.ReadOccurrencesAsync(context.ScopeId);
        Assert.Equal(
            committedOccurrence.OccurrenceId,
            Assert.Single(occurrencesAfterReplay).OccurrenceId);

        var recovered = await context.CoreStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(recovered);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            recovered.AlarmState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            recovered.EventState);
    }

    [Fact]
    public async Task EmptyDefinitionSetIsExplicitlyEvaluated()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        Assert.True((await context.ActivateEmptyDefinitionsAsync(
            RevisionNumber.Initial)).IsSuccess);
        var pending = await context.CreatePendingCutAsync(
            value: 42,
            RevisionNumber.Initial);
        var afterHistory = await context.CompleteHistoryAsync(pending);

        var processed = await context
            .CreateAlarmProcessor()
            .ProcessAsync(afterHistory);

        Assert.True(processed.IsSuccess);
        Assert.Equal(
            RuntimeAlarmDeliveryStatus.Evaluated,
            processed.Value.Status);
        Assert.Empty(processed.Value.Evaluation!.Occurrences);
        var progress = await context.CoreStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(progress);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            progress.AlarmState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            progress.EventState);
    }

    [Fact]
    public async Task GapSkipsAlarmEvaluationAndCompletesStage()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var pending = await context.CreatePendingGapAsync();
        var afterHistory = await context.CompleteHistoryAsync(pending);

        var processed = await context
            .CreateAlarmProcessor()
            .ProcessAsync(afterHistory);

        Assert.True(processed.IsSuccess);
        Assert.Equal(
            RuntimeAlarmDeliveryStatus.GapSkipped,
            processed.Value.Status);
        Assert.Null(processed.Value.Evaluation);
        Assert.Empty(await context.AlarmStore.ReadOccurrencesAsync(
            context.ScopeId));

        var progress = await context.CoreStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(progress);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            progress.HistoryState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            progress.AlarmState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            progress.EventState);
    }

    [Fact]
    public async Task InactiveDefinitionEpochPersistsFailureAndLeavesEventPending()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        Assert.True((await context.ActivateDefinitionsAsync(
            RevisionNumber.Initial,
            threshold: 100)).IsSuccess);
        var inactiveEpoch = RevisionNumber.From(2);
        var pending = await context.CreatePendingCutAsync(
            value: 120,
            inactiveEpoch);
        var afterHistory = await context.CompleteHistoryAsync(pending);

        var processed = await context
            .CreateAlarmProcessor()
            .ProcessAsync(afterHistory);

        Assert.Equal(
            "alarm.definition_epoch_inactive",
            processed.Error?.Code.Value);
        var failed = await context.CoreStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(failed);
        Assert.Equal(
            context.ConfigurationRevisionId,
            failed.DefinitionEpoch!.Value);
        Assert.Equal(inactiveEpoch, failed.AlarmDefinitionEpoch!.Value);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            failed.AlarmState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            failed.EventState);
        Assert.Equal(
            "alarm.definition_epoch_inactive",
            failed.LastErrorCode);
        Assert.Equal(Start, failed.LastErrorAt);
        Assert.Empty(await context.AlarmStore.ReadOccurrencesAsync(
            context.ScopeId));
    }

    private sealed class RuntimeTestContext : IAsyncDisposable
    {
        private static readonly SourceId SourceId =
            Dispatcher.Core.SourceId.From(
                Guid.Parse("d2000000-0000-0000-0000-000000000001"));
        private static readonly PointId TestPointId =
            Dispatcher.Semantics.PointId.From(
                Guid.Parse("d3000000-0000-0000-0000-000000000001"));
        private static readonly AlarmDefinitionId DefinitionId =
            AlarmDefinitionId.From(
                Guid.Parse("d4000000-0000-0000-0000-000000000001"));

        private RuntimeTestContext(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            FixedClock clock)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ScopeId = RuntimeScopeId.From(
                Guid.Parse("d5000000-0000-0000-0000-000000000001"));
            ConfigurationRevisionId =
                Guid.Parse("d6000000-0000-0000-0000-000000000001");
            PointId = TestPointId;
            Binding = new SourceBinding(
                ScopeId,
                SourceId,
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1));
            CoreStore = new CoreRuntimeStore(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock);
            HistoryStore = new HistoryStore(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock,
                new HistoryQueryLimits(
                    maxPageSize: 100,
                    maxAggregateBuckets: 100));
            AlarmStore = new AlarmStore(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock);
        }

        public TestDatabase Database { get; }

        public NpgsqlDataSource DataSource { get; }

        public FixedClock Clock { get; }

        public RuntimeScopeId ScopeId { get; }

        public Guid ConfigurationRevisionId { get; }

        public PointId PointId { get; }

        public SourceBinding Binding { get; }

        public CoreRuntimeStore CoreStore { get; }

        public HistoryStore HistoryStore { get; }

        public AlarmStore AlarmStore { get; }

        public static async Task<RuntimeTestContext> CreateAsync(
            PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(
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
            return new RuntimeTestContext(
                database,
                dataSource,
                new FixedClock(Start));
        }

        public Task<Result> ActivateDefinitionsAsync(
            RevisionNumber epoch,
            long threshold) =>
            AlarmStore.ActivateDefinitionSetAsync(
                new AlarmDefinitionSet(
                    ScopeId,
                    epoch,
                    [
                        new AlarmDefinition(
                            DefinitionId,
                            PointId,
                            "High power",
                            AlarmThresholdDirection.High,
                            threshold,
                            hysteresis: 10,
                            raiseDelay: TimeSpan.Zero,
                            clearDelay: TimeSpan.Zero),
                    ]));

        public Task<Result> ActivateEmptyDefinitionsAsync(
            RevisionNumber epoch) =>
            AlarmStore.ActivateDefinitionSetAsync(
                new AlarmDefinitionSet(
                    ScopeId,
                    epoch,
                    []));

        public RuntimeAlarmDeliveryProcessor CreateAlarmProcessor(
            RuntimeAlarmEvaluationCommitHook? hook = null) =>
            new(
                AlarmStore,
                CoreStore,
                hook);

        public async Task<RuntimeProcessingDelivery> CompleteHistoryAsync(
            RuntimeProcessingDelivery delivery)
        {
            var processed = await new RuntimeHistoryDeliveryProcessor(
                    HistoryStore,
                    CoreStore)
                .ProcessAsync(delivery);
            Assert.True(processed.IsSuccess);
            var progress = await CoreStore.LoadPendingDeliveryAsync(
                ScopeId);
            Assert.NotNull(progress);
            return progress;
        }

        public async Task<RuntimeProcessingDelivery> CreatePendingCutAsync(
            long value,
            RevisionNumber alarmDefinitionEpoch)
        {
            var runtime = CreateRuntime();
            Assert.True(runtime.ActivateBinding(Binding).IsSuccess);
            var cut = RuntimeCut.Normalize(
                Binding,
                1,
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
            var obligation = await CoreStore.AppendCutAsync(cut);
            var acceptance = runtime.Apply(cut).Value;
            var saved =
                await CoreStore.SaveCheckpointWithPendingDeliveryAsync(
                    runtime.CaptureCheckpoint(),
                    obligation,
                    acceptance,
                    protectedContinuity: true,
                    definitionEpoch: ConfigurationRevisionId,
                    alarmDefinitionEpoch: alarmDefinitionEpoch);
            Assert.True(saved.IsSuccess);
            return saved.Value;
        }

        public async Task<RuntimeProcessingDelivery> CreatePendingGapAsync()
        {
            var runtime = CreateRuntime();
            Assert.True(runtime.ActivateBinding(Binding).IsSuccess);
            var gap = new RuntimeSourceGap(
                Binding,
                1,
                1,
                3,
                "source_evidence_unavailable");
            var obligation = await CoreStore.AppendGapAsync(gap);
            Assert.True(runtime.ApplyGap(gap).IsSuccess);
            var saved =
                await CoreStore.SaveCheckpointWithPendingDeliveryAsync(
                    runtime.CaptureCheckpoint(),
                    obligation,
                    postCutAcceptance: null,
                    protectedContinuity: false);
            Assert.True(saved.IsSuccess);
            return saved.Value;
        }

        private CoreRuntime CreateRuntime() =>
            new(
                ScopeId,
                Clock,
                Clock,
                new RuntimeCurrentLimits(
                    maxPoints: 8,
                    retainedChangeCapacity: 16));

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
            TimeSpan.FromTicks(
                checked(finish.Value - start.Value));
    }
}
