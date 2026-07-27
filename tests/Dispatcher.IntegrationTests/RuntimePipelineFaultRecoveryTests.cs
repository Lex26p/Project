using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.History;
using Dispatcher.Persistence;
using Dispatcher.RuntimeHost;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class RuntimePipelineFaultRecoveryTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 27, 21, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public RuntimePipelineFaultRecoveryTests(
        PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    public enum PipelineFaultStage
    {
        History = 1,
        Alarm = 2,
        Event = 3,
    }

    [Theory]
    [InlineData(PipelineFaultStage.History)]
    [InlineData(PipelineFaultStage.Alarm)]
    [InlineData(PipelineFaultStage.Event)]
    public async Task CommittedStageCrashKeepsPublishedCursorAndRestartCompletesOnce(
        PipelineFaultStage faultStage)
    {
        await using var context =
            await RuntimeTestContext.CreateAsync(
                cluster,
                RuntimeTestContext.HighDefinition());

        using (var baselineHost = context.CreateHost())
        {
            Assert.True(
                (await baselineHost.StartAsync()).IsSuccess);
            Assert.True(
                baselineHost.ActivateBinding(
                    context.Binding).IsSuccess);
            await context.PublishCutAsync(
                baselineHost,
                scheduleSequence: 1,
                sourcePosition: 1,
                value: 50);
        }

        var baseline = await context.PublishedReader
            .ReadReadinessAsync(context.ScopeId);
        Assert.True(baseline.Published);
        Assert.Equal(
            (ulong)1,
            baseline.CompletedObligationPosition.Value);
        Assert.Equal((ulong)1, baseline.CurrentCursor.Value);

        context.Clock.Advance(TimeSpan.FromSeconds(1));
        using (var faultingHost =
               context.CreateHost(faultStage))
        {
            Assert.True(
                (await faultingHost.StartAsync()).IsSuccess);
            Assert.True(
                faultingHost.ActivateBinding(
                    context.Binding).IsSuccess);
            var queued = await faultingHost.EnqueueAsync(
                context.CreateCut(
                    scheduleSequence: 2,
                    sourcePosition: 2,
                    value: 110));
            Assert.True(queued.IsSuccess);
            Assert.Equal(
                RuntimeIngressStatus.Queued,
                queued.Value.Status);

            await Assert.ThrowsAsync<IOException>(
                () => faultingHost.ProcessNextDeliveryAsync());
        }

        var stillBaseline = await context.PublishedReader
            .ReadReadinessAsync(context.ScopeId);
        Assert.True(stillBaseline.Published);
        Assert.Equal(
            baseline.CompletedObligationPosition,
            stillBaseline.CompletedObligationPosition);
        Assert.Equal(
            baseline.CurrentCursor,
            stillBaseline.CurrentCursor);

        var pending = await context.CoreStore
            .LoadPendingDeliveryAsync(context.ScopeId);
        Assert.NotNull(pending);
        Assert.Equal(
            new OwnerPosition<RuntimeSourceObligation>(2),
            pending.ObligationPosition);
        Assert.Equal(
            faultStage == PipelineFaultStage.History
                ? RuntimeDownstreamDeliveryState.Pending
                : RuntimeDownstreamDeliveryState.Completed,
            pending.HistoryState);
        Assert.Equal(
            faultStage is PipelineFaultStage.History or
                PipelineFaultStage.Alarm
                ? RuntimeDownstreamDeliveryState.Pending
                : RuntimeDownstreamDeliveryState.Completed,
            pending.AlarmState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Pending,
            pending.EventState);

        var beforeRecovery =
            await context.ReadCountsAsync();
        Assert.Equal(2L, beforeRecovery.HistoryBatches);
        Assert.Equal(2L, beforeRecovery.HistorySamples);
        Assert.Equal(
            faultStage == PipelineFaultStage.History
                ? 0L
                : 1L,
            beforeRecovery.AlarmOccurrences);
        Assert.Equal(
            faultStage == PipelineFaultStage.Event
                ? 1L
                : 0L,
            beforeRecovery.EventJournal);
        Assert.Equal(
            faultStage == PipelineFaultStage.Event
                ? 1L
                : 0L,
            beforeRecovery.ProjectionChanges);
        Assert.Equal(1L, beforeRecovery.PublishedDeliveries);

        using (var recoveryHost = context.CreateHost())
        {
            var restarted =
                await recoveryHost.StartAsync();
            Assert.True(
                restarted.IsSuccess,
                restarted.Error?.Code.Value);
            Assert.Equal(
                RuntimeHostState.Running,
                recoveryHost.GetReadiness().State);
        }

        Assert.Null(
            await context.CoreStore.LoadPendingDeliveryAsync(
                context.ScopeId));
        var recovered = await context.PublishedReader
            .ReadReadinessAsync(context.ScopeId);
        Assert.Equal(
            (ulong)2,
            recovered.CompletedObligationPosition.Value);
        Assert.Equal((ulong)2, recovered.CurrentCursor.Value);

        var afterRecovery =
            await context.ReadCountsAsync();
        Assert.Equal(2L, afterRecovery.HistoryBatches);
        Assert.Equal(2L, afterRecovery.HistorySamples);
        Assert.Equal(1L, afterRecovery.AlarmOccurrences);
        Assert.Equal(1L, afterRecovery.EventJournal);
        Assert.Equal(1L, afterRecovery.EventProjection);
        Assert.Equal(1L, afterRecovery.ProjectionChanges);
        Assert.Equal(2L, afterRecovery.PublishedDeliveries);

        using (var duplicateRestart = context.CreateHost())
        {
            Assert.True(
                (await duplicateRestart.StartAsync()).IsSuccess);
        }

        Assert.Equal(
            afterRecovery,
            await context.ReadCountsAsync());
    }

    [Fact]
    public async Task RaiseAndClearProduceTwoConsistentOccurrenceVersionsAndEvents()
    {
        await using var context =
            await RuntimeTestContext.CreateAsync(
                cluster,
                RuntimeTestContext.HighDefinition());
        using var host = context.CreateHost();

        Assert.True((await host.StartAsync()).IsSuccess);
        Assert.True(
            host.ActivateBinding(context.Binding).IsSuccess);

        await context.PublishCutAsync(
            host,
            scheduleSequence: 1,
            sourcePosition: 1,
            value: 110);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        await context.PublishCutAsync(
            host,
            scheduleSequence: 2,
            sourcePosition: 2,
            value: 90);

        var occurrence = Assert.Single(
            await context.AlarmStore.ReadOccurrencesAsync(
                context.ScopeId));
        Assert.Equal(
            AlarmConditionState.Normal,
            occurrence.Condition.State);
        Assert.Equal(
            StateVersion.From(2),
            occurrence.Condition.Version);
        Assert.NotNull(occurrence.ClosedAt);
        Assert.NotNull(occurrence.Condition.ClearedAt);

        var events = await context.EventStore.QueryAsync(
            new EventQueryRequest(
                context.ScopeId,
                new HashSet<PointId>
                {
                    RuntimeTestContext.PointId,
                },
                new HashSet<OperationalEventKind>(),
                From: null,
                To: null,
                PageSize: 10));
        Assert.True(events.IsSuccess);
        Assert.Collection(
            events.Value.Events,
            raised =>
            {
                Assert.Equal(
                    OperationalEventKind.AlarmRaised,
                    raised.Kind);
                Assert.Equal(
                    StateVersion.Initial,
                    raised.SourceConditionVersion);
                Assert.Equal(
                    occurrence.OccurrenceId,
                    raised.OccurrenceId);
            },
            cleared =>
            {
                Assert.Equal(
                    OperationalEventKind.AlarmCleared,
                    cleared.Kind);
                Assert.Equal(
                    StateVersion.From(2),
                    cleared.SourceConditionVersion);
                Assert.Equal(
                    occurrence.OccurrenceId,
                    cleared.OccurrenceId);
            });

        var readiness = await context.PublishedReader
            .ReadReadinessAsync(context.ScopeId);
        Assert.Equal(
            (ulong)2,
            readiness.CompletedObligationPosition.Value);
        Assert.Equal((ulong)2, readiness.CurrentCursor.Value);

        var counts = await context.ReadCountsAsync();
        Assert.Equal(2L, counts.HistoryBatches);
        Assert.Equal(2L, counts.HistorySamples);
        Assert.Equal(1L, counts.AlarmOccurrences);
        Assert.Equal(2L, counts.EventJournal);
        Assert.Equal(1L, counts.EventProjection);
        Assert.Equal(2L, counts.ProjectionChanges);
        Assert.Equal(2L, counts.PublishedDeliveries);
    }

    [Fact]
    public async Task BoundedIngressGapWithEmptyDefinitionSetCreatesNoAlarmOrEvent()
    {
        await using var context =
            await RuntimeTestContext.CreateAsync(
                cluster,
                definitions: []);
        using var host = context.CreateHost(
            ingressCapacity: 1);

        Assert.True((await host.StartAsync()).IsSuccess);
        Assert.True(
            host.ActivateBinding(context.Binding).IsSuccess);

        var first = await host.EnqueueAsync(
            context.CreateCut(
                scheduleSequence: 1,
                sourcePosition: 1,
                value: 40));
        Assert.True(first.IsSuccess);
        Assert.Equal(
            RuntimeIngressStatus.Queued,
            first.Value.Status);

        var overflow = await host.EnqueueAsync(
            context.CreateCut(
                scheduleSequence: 2,
                sourcePosition: 2,
                value: 41));
        Assert.True(overflow.IsSuccess);
        Assert.Equal(
            RuntimeIngressStatus.GapRecorded,
            overflow.Value.Status);
        Assert.NotNull(overflow.Value.GapObligation);

        var sample = await host.ProcessNextDeliveryAsync();
        Assert.True(sample.IsSuccess);
        Assert.Equal(
            RuntimeProcessNextStatus.Published,
            sample.Value.Status);

        var gap = await host.ProcessNextDeliveryAsync();
        Assert.True(gap.IsSuccess);
        Assert.Equal(
            RuntimeProcessNextStatus.Published,
            gap.Value.Status);
        Assert.Equal(
            RuntimeFactClass.SourceGap,
            gap.Value.Obligation!.FactClass);

        var counts = await context.ReadCountsAsync();
        Assert.Equal(2L, counts.HistoryBatches);
        Assert.Equal(1L, counts.HistorySamples);
        Assert.Equal(1L, counts.HistoryGaps);
        Assert.Equal(0L, counts.AlarmOccurrences);
        Assert.Equal(0L, counts.EventJournal);
        Assert.Equal(0L, counts.EventProjection);
        Assert.Equal(0L, counts.ProjectionChanges);
        Assert.Equal(2L, counts.PublishedDeliveries);

        var readiness = await context.PublishedReader
            .ReadReadinessAsync(context.ScopeId);
        Assert.Equal(
            (ulong)2,
            readiness.CompletedObligationPosition.Value);
        Assert.Equal((ulong)1, readiness.CurrentCursor.Value);
        Assert.False(readiness.ProtectedContinuity);
        Assert.False(readiness.Ready);
        Assert.Equal(
            "runtime.protected_continuity_lost",
            readiness.DegradationReasonCode);
    }

    private sealed class RuntimeTestContext : IAsyncDisposable
    {
        private static readonly SourceId SourceId =
            Dispatcher.Core.SourceId.From(
                Guid.Parse(
                    "e0000000-0000-0000-0000-000000000001"));
        private static readonly PointId TestPointId =
            Dispatcher.Semantics.PointId.From(
                Guid.Parse(
                    "e1000000-0000-0000-0000-000000000001"));
        private static readonly AlarmDefinitionId DefinitionId =
            AlarmDefinitionId.From(
                Guid.Parse(
                    "e2000000-0000-0000-0000-000000000001"));

        private RuntimeTestContext(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            MutableClock clock)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ScopeId = RuntimeScopeId.From(
                Guid.Parse(
                    "e3000000-0000-0000-0000-000000000001"));
            ConfigurationRevisionId = Guid.Parse(
                "e4000000-0000-0000-0000-000000000001");
            Binding = new SourceBinding(
                ScopeId,
                SourceId,
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1));
            CoreStore = new CoreRuntimeStore(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock);
            AlarmStore = CreateAlarmStore();
            EventStore = CreateEventStore();
            PublishedReader =
                new CoreRuntimePublishedReader(
                    DataSource,
                    PostgreSqlClusterFixture.OwnerBRole);
        }

        public TestDatabase Database { get; }

        public NpgsqlDataSource DataSource { get; }

        public MutableClock Clock { get; }

        public RuntimeScopeId ScopeId { get; }

        public Guid ConfigurationRevisionId { get; }

        public static PointId PointId => TestPointId;

        public SourceBinding Binding { get; }

        public CoreRuntimeStore CoreStore { get; }

        public AlarmStore AlarmStore { get; }

        public EventStore EventStore { get; }

        public CoreRuntimePublishedReader PublishedReader { get; }

        public static IReadOnlyCollection<AlarmDefinition>
            HighDefinition() =>
            [
                new AlarmDefinition(
                    DefinitionId,
                    TestPointId,
                    "High power",
                    AlarmThresholdDirection.High,
                    threshold: 100,
                    hysteresis: 0,
                    raiseDelay: TimeSpan.Zero,
                    clearDelay: TimeSpan.Zero,
                    priority: AlarmPriority.High),
            ];

        public static async Task<RuntimeTestContext>
            CreateAsync(
                PostgreSqlClusterFixture cluster,
                IReadOnlyCollection<AlarmDefinition> definitions)
        {
            var database =
                await cluster.CreateDatabaseAsync();
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
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                EventMigrations.CreatePlan(
                    PostgreSqlClusterFixture.OwnerBRole));

            var context = new RuntimeTestContext(
                database,
                dataSource,
                new MutableClock(Start));
            Assert.True(
                (await context.AlarmStore
                    .ActivateDefinitionSetAsync(
                        new AlarmDefinitionSet(
                            context.ScopeId,
                            RevisionNumber.Initial,
                            definitions))).IsSuccess);
            return context;
        }

        public CoreRuntimeHost CreateHost(
            PipelineFaultStage? faultStage = null,
            int ingressCapacity = 4)
        {
            var historyStore = new HistoryStore(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock,
                new HistoryQueryLimits(
                    maxPageSize: 100,
                    maxAggregateBuckets: 100),
                faultStage == PipelineFaultStage.History
                    ? new HistoryIngestCommitHook(
                        static (_, _) =>
                            throw new IOException(
                                "simulated crash after History commit"))
                    : null);
            var alarmStore = CreateAlarmStore();
            var eventStore = CreateEventStore();
            var coordinator = new RuntimeDeliveryCoordinator(
                CoreStore,
                new RuntimeHistoryDeliveryProcessor(
                    historyStore,
                    CoreStore),
                new RuntimeAlarmDeliveryProcessor(
                    alarmStore,
                    CoreStore,
                    faultStage == PipelineFaultStage.Alarm
                        ? new RuntimeAlarmEvaluationCommitHook(
                            static (_, _) =>
                                throw new IOException(
                                    "simulated crash after Alarm commit"))
                        : null),
                new RuntimeEventDeliveryProcessor(
                    alarmStore,
                    eventStore,
                    CoreStore,
                    faultStage == PipelineFaultStage.Event
                        ? new RuntimeEventAcceptanceCommitHook(
                            static (_, _) =>
                                throw new IOException(
                                    "simulated crash after Event commit"))
                        : null),
                ConfigurationRevisionId,
                RevisionNumber.Initial,
                new RuntimeDownstreamRetryPolicy(
                    maxAttempts: 1,
                    initialBackoff:
                        TimeSpan.FromMilliseconds(10),
                    maximumBackoff:
                        TimeSpan.FromMilliseconds(10)),
                static _ => false);

            return new CoreRuntimeHost(
                ScopeId,
                new CoreRuntime(
                    ScopeId,
                    Clock,
                    Clock,
                    new RuntimeCurrentLimits(
                        maxPoints: 8,
                        retainedChangeCapacity: 32)),
                CoreStore,
                new RuntimeIngressLimits(ingressCapacity),
                deliveryProcessor:
                    coordinator.CreateDeliveryProcessor());
        }

        public RuntimeCut CreateCut(
            ulong scheduleSequence,
            ulong sourcePosition,
            long value) =>
            RuntimeCut.Normalize(
                Binding,
                scheduleSequence,
                [
                    new SourceObservation(
                        ScopeId,
                        SourceId,
                        TestPointId,
                        new OwnerPosition<SourceObservation>(
                            sourcePosition),
                        TypedValue.From(value),
                        Unit.FromSymbol("kW"),
                        DataQuality.Good,
                        Freshness.Fresh,
                        SourceTimestamp.FromUtc(
                            Clock.GetUtcNow())),
                ]).Value;

        public async Task PublishCutAsync(
            CoreRuntimeHost host,
            ulong scheduleSequence,
            ulong sourcePosition,
            long value)
        {
            var queued = await host.EnqueueAsync(
                CreateCut(
                    scheduleSequence,
                    sourcePosition,
                    value));
            Assert.True(queued.IsSuccess);
            Assert.Equal(
                RuntimeIngressStatus.Queued,
                queued.Value.Status);
            var processed =
                await host.ProcessNextDeliveryAsync();
            Assert.True(
                processed.IsSuccess,
                processed.Error?.Code.Value);
            Assert.Equal(
                RuntimeProcessNextStatus.Published,
                processed.Value.Status);
        }

        public async Task<PipelineCounts> ReadCountsAsync()
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
                     FROM {HistoryMigrations.Schema}.ingest_batch),
                    (SELECT count(*)
                     FROM {HistoryMigrations.Schema}.sample),
                    (SELECT count(*)
                     FROM {HistoryMigrations.Schema}.gap),
                    (SELECT count(*)
                     FROM {AlarmMigrations.Schema}.occurrence),
                    (SELECT count(*)
                     FROM {EventMigrations.Schema}.journal_event),
                    (SELECT count(*)
                     FROM {EventMigrations.Schema}.occurrence_projection),
                    (SELECT count(*)
                     FROM {EventMigrations.Schema}.projection_change),
                    (SELECT count(*)
                     FROM {CoreRuntimeMigrations.Schema}.processing_delivery
                     WHERE stage = 3);
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
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7));
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }

        private AlarmStore CreateAlarmStore() =>
            new(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock);

        private EventStore CreateEventStore() =>
            new(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock,
                new EventDispatcherLimits(
                    maxPageSize: 100,
                    retainedProjectionChanges: 100,
                    maxFeedChanges: 100));
    }

    private sealed record PipelineCounts(
        long HistoryBatches,
        long HistorySamples,
        long HistoryGaps,
        long AlarmOccurrences,
        long EventJournal,
        long EventProjection,
        long ProjectionChanges,
        long PublishedDeliveries);

    private sealed class MutableClock :
        IWallClock,
        IMonotonicClock
    {
        private DateTimeOffset now;

        public MutableClock(DateTimeOffset now)
        {
            this.now = now;
        }

        public DateTimeOffset GetUtcNow() => now;

        public MonotonicTimestamp GetTimestamp() =>
            new(now.UtcTicks);

        public TimeSpan GetElapsedTime(
            MonotonicTimestamp start,
            MonotonicTimestamp finish) =>
            TimeSpan.FromTicks(
                checked(finish.Value - start.Value));

        public void Advance(TimeSpan duration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(
                duration,
                TimeSpan.Zero);
            now = now.Add(duration);
        }
    }
}
