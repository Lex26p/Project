using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Persistence;
using Dispatcher.RuntimeHost;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class RuntimeEventDeliveryProcessorTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 27, 19, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public RuntimeEventDeliveryProcessorTests(
        PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task RaisedOccurrenceProjectsEventAndCompletesEventStage()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var pending = await context.CreateAlarmCompletedCutAsync(
            value: 110,
            definitions: RuntimeTestContext.Definitions(threshold: 100));

        var projected = await context.CreateEventProcessor().ProcessAsync(
            pending);

        Assert.True(projected.IsSuccess);
        Assert.Equal(
            RuntimeEventDeliveryStatus.Projected,
            projected.Value.Status);
        var accepted = Assert.Single(projected.Value.Acceptances);
        Assert.True(accepted.EventInserted);
        Assert.True(accepted.ProjectionChanged);
        Assert.Equal(
            OperationalEventKind.AlarmRaised,
            accepted.Event.Kind);

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
            RuntimeDownstreamDeliveryState.Completed,
            progress.EventState);
        Assert.Equal(
            RuntimeProcessingDeliveryStage.PendingDownstream,
            progress.Stage);
        Assert.Null(progress.LastErrorCode);

        var repeated = await context.CreateEventProcessor().ProcessAsync(
            progress);
        Assert.True(repeated.IsSuccess);
        Assert.Equal(
            RuntimeEventDeliveryStatus.AlreadyCompleted,
            repeated.Value.Status);

        Assert.Equal(
            new EventCounts(1, 1, 1),
            await context.ReadEventCountsAsync());
    }

    [Fact]
    public async Task CrashAfterEventCommitReplaysWithoutDuplicateJournalRecord()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var pending = await context.CreateAlarmCompletedCutAsync(
            value: 120,
            definitions: RuntimeTestContext.Definitions(threshold: 100));
        var crashing = context.CreateEventProcessor(
            new RuntimeEventAcceptanceCommitHook(
                (_, _) => throw new InvalidOperationException(
                    "simulated crash after Event commit")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => crashing.ProcessAsync(pending));

        Assert.Equal(
            new EventCounts(1, 1, 1),
            await context.ReadEventCountsAsync());
        var afterCrash = await context.CoreStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(afterCrash);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            afterCrash.EventState);
        Assert.Null(afterCrash.LastErrorCode);

        var replay = await context.CreateEventProcessor().ProcessAsync(
            afterCrash);

        Assert.True(replay.IsSuccess);
        Assert.Equal(
            RuntimeEventDeliveryStatus.Duplicate,
            replay.Value.Status);
        var duplicate = Assert.Single(replay.Value.Acceptances);
        Assert.False(duplicate.EventInserted);
        Assert.False(duplicate.ProjectionChanged);
        Assert.Equal(
            new EventCounts(1, 1, 1),
            await context.ReadEventCountsAsync());

        var recovered = await context.CoreStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(recovered);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            recovered.EventState);
    }

    [Fact]
    public async Task GapCompletesEventWithoutJournalRecord()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var pending = await context.CreateAlarmCompletedGapAsync();

        var processed = await context.CreateEventProcessor().ProcessAsync(
            pending);

        Assert.True(processed.IsSuccess);
        Assert.Equal(
            RuntimeEventDeliveryStatus.GapSkipped,
            processed.Value.Status);
        Assert.Empty(processed.Value.Acceptances);
        Assert.Equal(
            new EventCounts(0, 0, 0),
            await context.ReadEventCountsAsync());

        var progress = await context.CoreStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(progress);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            progress.EventState);
    }

    [Fact]
    public async Task EmptyDefinitionSetCompletesEventWithoutProjection()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var pending = await context.CreateAlarmCompletedCutAsync(
            value: 50,
            definitions: []);

        var processed = await context.CreateEventProcessor().ProcessAsync(
            pending);

        Assert.True(processed.IsSuccess);
        Assert.Equal(
            RuntimeEventDeliveryStatus.NoOccurrences,
            processed.Value.Status);
        Assert.Empty(processed.Value.Acceptances);
        Assert.Equal(
            new EventCounts(0, 0, 0),
            await context.ReadEventCountsAsync());

        var progress = await context.CoreStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(progress);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            progress.EventState);
    }

    [Fact]
    public async Task SourceConflictPersistsSafeFailureAndLeavesEventPending()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var pending = await context.CreateAlarmCompletedCutAsync(
            value: 130,
            definitions: RuntimeTestContext.Definitions(threshold: 100));
        var occurrence = Assert.Single(
            await context.AlarmStore.ReadOccurrencesAsync(
                context.ScopeId));
        var conflicting = occurrence with
        {
            Priority = occurrence.Priority == AlarmPriority.Critical
                ? AlarmPriority.Low
                : AlarmPriority.Critical,
        };
        Assert.True((await context.EventStore.AcceptAlarmOccurrenceAsync(
            conflicting)).IsSuccess);

        var processed = await context.CreateEventProcessor().ProcessAsync(
            pending);

        Assert.Equal(
            "event.source_conflict",
            processed.Error?.Code.Value);
        var failed = await context.CoreStore.LoadPendingDeliveryAsync(
            context.ScopeId);
        Assert.NotNull(failed);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            failed.EventState);
        Assert.Equal("event.source_conflict", failed.LastErrorCode);
        Assert.Equal(Start, failed.LastErrorAt);
        Assert.Equal(
            new EventCounts(1, 1, 1),
            await context.ReadEventCountsAsync());
    }

    private sealed class RuntimeTestContext : IAsyncDisposable
    {
        private static readonly SourceId SourceId =
            Dispatcher.Core.SourceId.From(
                Guid.Parse("d7000000-0000-0000-0000-000000000001"));
        private static readonly PointId PointId =
            Dispatcher.Semantics.PointId.From(
                Guid.Parse("d8000000-0000-0000-0000-000000000001"));
        private static readonly AlarmDefinitionId DefinitionId =
            AlarmDefinitionId.From(
                Guid.Parse("d9000000-0000-0000-0000-000000000001"));
        private static readonly Guid ConfigurationRevisionId =
            Guid.Parse("da000000-0000-0000-0000-000000000001");

        private RuntimeTestContext(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            FixedClock clock)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ScopeId = RuntimeScopeId.From(
                Guid.Parse("db000000-0000-0000-0000-000000000001"));
            Binding = new SourceBinding(
                ScopeId,
                SourceId,
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1));
            CoreStore = new CoreRuntimeStore(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock);
            AlarmStore = new AlarmStore(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock);
            EventStore = new EventStore(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock,
                new EventDispatcherLimits(
                    maxPageSize: 100,
                    retainedProjectionChanges: 100,
                    maxFeedChanges: 100));
        }

        public TestDatabase Database { get; }

        public NpgsqlDataSource DataSource { get; }

        public FixedClock Clock { get; }

        public RuntimeScopeId ScopeId { get; }

        public SourceBinding Binding { get; }

        public CoreRuntimeStore CoreStore { get; }

        public AlarmStore AlarmStore { get; }

        public EventStore EventStore { get; }

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
                AlarmMigrations.CreatePlan(
                    PostgreSqlClusterFixture.OwnerBRole));
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                EventMigrations.CreatePlan(
                    PostgreSqlClusterFixture.OwnerBRole));
            return new RuntimeTestContext(
                database,
                dataSource,
                new FixedClock(Start));
        }

        public static IReadOnlyCollection<AlarmDefinition> Definitions(
            long threshold) =>
            [
                new AlarmDefinition(
                    DefinitionId,
                    PointId,
                    "High power",
                    AlarmThresholdDirection.High,
                    threshold,
                    hysteresis: 0,
                    raiseDelay: TimeSpan.Zero,
                    clearDelay: TimeSpan.Zero,
                    priority: AlarmPriority.High),
            ];

        public RuntimeEventDeliveryProcessor CreateEventProcessor(
            RuntimeEventAcceptanceCommitHook? hook = null) =>
            new(
                AlarmStore,
                EventStore,
                CoreStore,
                hook);

        public async Task<RuntimeProcessingDelivery>
            CreateAlarmCompletedCutAsync(
                long value,
                IReadOnlyCollection<AlarmDefinition> definitions)
        {
            Assert.True((await AlarmStore.ActivateDefinitionSetAsync(
                new AlarmDefinitionSet(
                    ScopeId,
                    RevisionNumber.Initial,
                    definitions))).IsSuccess);

            var runtime = CreateRuntime();
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
                        TypedValue.From((decimal)value),
                        Unit.FromSymbol("kW"),
                        DataQuality.Good,
                        Freshness.Fresh,
                        SourceTimestamp.FromUtc(Start.AddSeconds(1))),
                ]).Value;
            var obligation = await CoreStore.AppendCutAsync(cut);
            var acceptance = runtime.Apply(cut).Value;
            var saved = await CoreStore.SaveCheckpointWithPendingDeliveryAsync(
                runtime.CaptureCheckpoint(),
                obligation,
                acceptance,
                protectedContinuity: true,
                definitionEpoch: ConfigurationRevisionId,
                alarmDefinitionEpoch: RevisionNumber.Initial);
            Assert.True(saved.IsSuccess);

            Assert.True((await CoreStore.CompleteDownstreamStageAsync(
                ScopeId,
                obligation.Position,
                RuntimeDownstreamStage.History)).IsSuccess);
            var afterHistory = await CoreStore.LoadPendingDeliveryAsync(
                ScopeId);
            Assert.NotNull(afterHistory);
            Assert.True((await new RuntimeAlarmDeliveryProcessor(
                    AlarmStore,
                    CoreStore)
                .ProcessAsync(afterHistory)).IsSuccess);

            var afterAlarm = await CoreStore.LoadPendingDeliveryAsync(
                ScopeId);
            Assert.NotNull(afterAlarm);
            return afterAlarm;
        }

        public async Task<RuntimeProcessingDelivery>
            CreateAlarmCompletedGapAsync()
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

            Assert.True((await CoreStore.CompleteDownstreamStageAsync(
                ScopeId,
                obligation.Position,
                RuntimeDownstreamStage.History)).IsSuccess);
            var afterHistory = await CoreStore.LoadPendingDeliveryAsync(
                ScopeId);
            Assert.NotNull(afterHistory);
            Assert.True((await new RuntimeAlarmDeliveryProcessor(
                    AlarmStore,
                    CoreStore)
                .ProcessAsync(afterHistory)).IsSuccess);

            var afterAlarm = await CoreStore.LoadPendingDeliveryAsync(
                ScopeId);
            Assert.NotNull(afterAlarm);
            return afterAlarm;
        }

        public async Task<EventCounts> ReadEventCountsAsync()
        {
            await using var connection =
                await DataSource.OpenConnectionAsync();
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
                     FROM {EventMigrations.Schema}.journal_event),
                    (SELECT count(*)
                     FROM {EventMigrations.Schema}.occurrence_projection),
                    (SELECT count(*)
                     FROM {EventMigrations.Schema}.projection_change);
                """,
                connection,
                transaction);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return new EventCounts(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2));
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

    private sealed record EventCounts(
        long JournalEvents,
        long ProjectionHeads,
        long ProjectionChanges);

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
