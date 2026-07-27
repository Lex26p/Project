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
public sealed class RuntimeDeliveryCoordinatorTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 27, 20, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public RuntimeDeliveryCoordinatorTests(
        PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task TransientCrashAfterHistoryCommitRetriesWithoutDuplicate()
    {
        await using var context =
            await RuntimeTestContext.CreateAsync(cluster);
        var pending =
            await context.CreatePendingCutAsync(value: 42);
        var hookCalls = 0;
        var delays = new List<TimeSpan>();
        var historyStore = context.CreateHistoryStore(
            new HistoryIngestCommitHook(
                (_, _) =>
                {
                    if (Interlocked.Increment(
                            ref hookCalls) == 1)
                    {
                        throw new IOException(
                            "simulated transient crash after History commit");
                    }

                    return Task.CompletedTask;
                }));
        var coordinator = context.CreateCoordinator(
            historyStore,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var processed = await coordinator.ProcessAsync(
            pending);

        Assert.True(
            processed.IsSuccess,
            processed.Error?.Code.Value);
        Assert.Equal(1, hookCalls);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(10)],
            delays);

        var progress =
            await context.CoreStore.LoadPendingDeliveryAsync(
                context.ScopeId);
        Assert.NotNull(progress);
        Assert.Equal(
            context.ConfigurationRevisionId,
            progress.DefinitionEpoch);
        Assert.Equal(
            RevisionNumber.Initial,
            progress.AlarmDefinitionEpoch);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            progress.HistoryState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            progress.AlarmState);
        Assert.Equal(
            RuntimeDownstreamDeliveryState.Completed,
            progress.EventState);
        Assert.Null(progress.LastErrorCode);
        Assert.Equal(1L, await context.CountHistorySamplesAsync());

        var completed = await context.CoreStore.CompleteDownstreamAsync(
            context.ScopeId,
            pending.ObligationPosition);
        Assert.True(completed.IsSuccess);
    }

    private sealed class RuntimeTestContext : IAsyncDisposable
    {
        private static readonly SourceId SourceId =
            Dispatcher.Core.SourceId.From(
                Guid.Parse(
                    "dc000000-0000-0000-0000-000000000001"));
        private static readonly PointId PointId =
            Dispatcher.Semantics.PointId.From(
                Guid.Parse(
                    "dd000000-0000-0000-0000-000000000001"));

        private RuntimeTestContext(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            FixedClock clock)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ScopeId = RuntimeScopeId.From(
                Guid.Parse(
                    "de000000-0000-0000-0000-000000000001"));
            ConfigurationRevisionId = Guid.Parse(
                "df000000-0000-0000-0000-000000000001");
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

        public Guid ConfigurationRevisionId { get; }

        public SourceBinding Binding { get; }

        public CoreRuntimeStore CoreStore { get; }

        public AlarmStore AlarmStore { get; }

        public EventStore EventStore { get; }

        public static async Task<RuntimeTestContext> CreateAsync(
            PostgreSqlClusterFixture cluster)
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
                new FixedClock(Start));
            Assert.True(
                (await context.AlarmStore.ActivateDefinitionSetAsync(
                    new AlarmDefinitionSet(
                        context.ScopeId,
                        RevisionNumber.Initial,
                        []))).IsSuccess);
            return context;
        }

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

        public RuntimeDeliveryCoordinator CreateCoordinator(
            HistoryStore historyStore,
            RuntimeDownstreamRetryDelay delay) =>
            new(
                CoreStore,
                new RuntimeHistoryDeliveryProcessor(
                    historyStore,
                    CoreStore),
                new RuntimeAlarmDeliveryProcessor(
                    AlarmStore,
                    CoreStore),
                new RuntimeEventDeliveryProcessor(
                    AlarmStore,
                    EventStore,
                    CoreStore),
                ConfigurationRevisionId,
                RevisionNumber.Initial,
                new RuntimeDownstreamRetryPolicy(
                    maxAttempts: 2,
                    initialBackoff:
                        TimeSpan.FromMilliseconds(10),
                    maximumBackoff:
                        TimeSpan.FromMilliseconds(20)),
                static exception => exception is IOException,
                delay);

        public async Task<RuntimeProcessingDelivery>
            CreatePendingCutAsync(long value)
        {
            var runtime = new CoreRuntime(
                ScopeId,
                Clock,
                Clock,
                new RuntimeCurrentLimits(
                    maxPoints: 8,
                    retainedChangeCapacity: 16));
            Assert.True(
                runtime.ActivateBinding(Binding).IsSuccess);
            var cut = RuntimeCut.Normalize(
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
                        SourceTimestamp.FromUtc(
                            Start.AddSeconds(1))),
                ]).Value;
            var obligation =
                await CoreStore.AppendCutAsync(cut);
            var acceptance = runtime.Apply(cut).Value;
            var saved =
                await CoreStore.SaveCheckpointWithPendingDeliveryAsync(
                    runtime.CaptureCheckpoint(),
                    obligation,
                    acceptance,
                    protectedContinuity: true);
            Assert.True(saved.IsSuccess);
            return saved.Value;
        }

        public async Task<long> CountHistorySamplesAsync()
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
                $"SELECT count(*) FROM {HistoryMigrations.Schema}.sample;",
                connection,
                transaction);
            return (long)(
                await command.ExecuteScalarAsync() ?? 0L);
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
            TimeSpan.FromTicks(
                checked(finish.Value - start.Value));
    }
}
