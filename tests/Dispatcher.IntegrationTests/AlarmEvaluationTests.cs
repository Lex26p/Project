using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Persistence;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class AlarmEvaluationTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public AlarmEvaluationTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task DefinitionEpochsAreContiguousIdempotentAndImmutable()
    {
        await using var context = await AlarmTestContext.CreateAsync(cluster);
        var first = context.DefinitionSet(RevisionNumber.Initial, threshold: 100);

        Assert.True((await context.Store.ActivateDefinitionSetAsync(first)).IsSuccess);
        Assert.True((await context.Store.ActivateDefinitionSetAsync(first)).IsSuccess);
        Assert.Equal(
            "alarm.definition_epoch_conflict",
            (await context.Store.ActivateDefinitionSetAsync(
                context.DefinitionSet(RevisionNumber.Initial, threshold: 101))).Error?.Code.Value);
        Assert.Equal(
            "alarm.definition_epoch",
            (await context.Store.ActivateDefinitionSetAsync(
                context.DefinitionSet(RevisionNumber.From(3), threshold: 103))).Error?.Code.Value);
        Assert.True((await context.Store.ActivateDefinitionSetAsync(
            context.DefinitionSet(RevisionNumber.From(2), threshold: 102))).IsSuccess);

        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var setRole = new NpgsqlCommand(
            $"SET LOCAL ROLE \"{PostgreSqlClusterFixture.OwnerBRole}\";",
            connection,
            transaction);
        await setRole.ExecuteNonQueryAsync();
        await using var mutation = new NpgsqlCommand(
            $"UPDATE {AlarmMigrations.Schema}.definition SET threshold = 999 WHERE scope_id = @scope_id;",
            connection,
            transaction);
        mutation.Parameters.AddWithValue("scope_id", context.ScopeId.Value);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => mutation.ExecuteNonQueryAsync());
        Assert.Contains("immutable", exception.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostCutEvaluationPreservesHysteresisTimersAndRestartState()
    {
        await using var context = await AlarmTestContext.CreateAsync(cluster);
        var epoch = RevisionNumber.Initial;
        Assert.True((await context.Store.ActivateDefinitionSetAsync(
            context.DefinitionSet(epoch, threshold: 100))).IsSuccess);
        var evaluator = new AlarmEvaluator(context.ScopeId, epoch, context.Store);

        Assert.Empty((await context.EvaluateAsync(evaluator, 110)).Occurrences);
        context.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Empty((await context.EvaluateAsync(evaluator, 111)).Occurrences);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var raised = Assert.Single((await context.EvaluateAsync(evaluator, 112)).Occurrences);
        Assert.Equal(AlarmConditionState.Active, raised.Condition.State);
        Assert.Equal(Start.AddSeconds(10), raised.OpenedAt);

        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var insideHysteresis = Assert.Single((await context.EvaluateAsync(evaluator, 95)).Occurrences);
        Assert.Equal(AlarmConditionState.Active, insideHysteresis.Condition.State);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var pendingClear = Assert.Single((await context.EvaluateAsync(evaluator, 90)).Occurrences);
        Assert.Equal(AlarmConditionState.PendingClear, pendingClear.Condition.State);

        var restartedStore = new AlarmStore(
            context.DataSource,
            PostgreSqlClusterFixture.OwnerBRole,
            context.Clock);
        var restartedEvaluator = new AlarmEvaluator(context.ScopeId, epoch, restartedStore);
        context.Clock.Advance(TimeSpan.FromSeconds(4));
        var stillPending = Assert.Single((await context.EvaluateAsync(restartedEvaluator, 89)).Occurrences);
        Assert.Equal(AlarmConditionState.PendingClear, stillPending.Condition.State);
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var finalInput = context.Apply(88);
        var cleared = Assert.Single((await restartedEvaluator.EvaluatePostRuntimeCutAsync(
            finalInput.Acceptance,
            finalInput.Snapshot)).Value.Occurrences);
        Assert.Equal(AlarmConditionState.Normal, cleared.Condition.State);
        Assert.Equal(context.Clock.GetUtcNow(), cleared.ClosedAt);

        var competingEvaluator = new AlarmEvaluator(context.ScopeId, epoch, restartedStore);
        var replay = await competingEvaluator.EvaluatePostRuntimeCutAsync(
            finalInput.Acceptance,
            finalInput.Snapshot);
        Assert.Single(replay.Value.Occurrences);
        Assert.Equal(cleared.OccurrenceId, replay.Value.Occurrences[0].OccurrenceId);
        Assert.Equal(StateVersion.Initial, cleared.Acknowledgement.Version);
        Assert.Equal(StateVersion.Initial, cleared.Assignment.Version);
        Assert.Equal(StateVersion.Initial, cleared.Shelving.Version);
        Assert.Equal(StateVersion.Initial, cleared.Suppression.Version);
        Assert.Equal(AlarmAcknowledgementState.Unacknowledged, cleared.Acknowledgement.State);
        Assert.Null(cleared.Assignment.AssignedTo);
        Assert.Null(cleared.Shelving.ShelvedUntil);
        Assert.False(cleared.Suppression.IsSuppressed);
    }

    private sealed class AlarmTestContext : IAsyncDisposable
    {
        private static readonly SourceId SourceId = Dispatcher.Core.SourceId.From(
            Guid.Parse("c2000000-0000-7000-8000-000000000001"));
        private static readonly PointId PointId = Dispatcher.Semantics.PointId.From(
            Guid.Parse("c3000000-0000-7000-8000-000000000001"));
        private static readonly AlarmDefinitionId DefinitionId = AlarmDefinitionId.From(
            Guid.Parse("c4000000-0000-7000-8000-000000000001"));
        private ulong sourcePosition;
        private ulong scheduleSequence;

        private AlarmTestContext(TestDatabase database, NpgsqlDataSource dataSource, MutableClock clock)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ScopeId = RuntimeScopeId.From(Guid.Parse("c1000000-0000-7000-8000-000000000001"));
            Binding = new SourceBinding(
                ScopeId,
                SourceId,
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1));
            Runtime = new CoreRuntime(
                ScopeId,
                Clock,
                Clock,
                new RuntimeCurrentLimits(maxPoints: 4, retainedChangeCapacity: 32));
            Assert.True(Runtime.ActivateBinding(Binding).IsSuccess);
            Store = new AlarmStore(DataSource, PostgreSqlClusterFixture.OwnerBRole, Clock);
        }

        public TestDatabase Database { get; }

        public NpgsqlDataSource DataSource { get; }

        public MutableClock Clock { get; }

        public RuntimeScopeId ScopeId { get; }

        public SourceBinding Binding { get; }

        public CoreRuntime Runtime { get; }

        public AlarmStore Store { get; }

        public static async Task<AlarmTestContext> CreateAsync(PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                AlarmMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
            return new AlarmTestContext(database, dataSource, new MutableClock(Start));
        }

        public AlarmDefinitionSet DefinitionSet(RevisionNumber epoch, long threshold) => new(
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
                    raiseDelay: TimeSpan.FromSeconds(10),
                    clearDelay: TimeSpan.FromSeconds(5)),
            ]);

        public async Task<AlarmEvaluationSnapshot> EvaluateAsync(AlarmEvaluator evaluator, long value)
        {
            var input = Apply(value);
            return (await evaluator.EvaluatePostRuntimeCutAsync(input.Acceptance, input.Snapshot)).Value;
        }

        public EvaluationInput Apply(long value)
        {
            sourcePosition = checked(sourcePosition + 1);
            scheduleSequence = checked(scheduleSequence + 1);
            var observation = new SourceObservation(
                ScopeId,
                SourceId,
                PointId,
                new OwnerPosition<SourceObservation>(sourcePosition),
                TypedValue.From(value),
                Unit.FromSymbol("kW"),
                DataQuality.Good,
                Freshness.Fresh,
                SourceTimestamp.FromUtc(Clock.GetUtcNow()));
            var cut = RuntimeCut.Normalize(Binding, scheduleSequence, [observation]).Value;
            var acceptance = Runtime.Apply(cut).Value;
            return new EvaluationInput(acceptance, Runtime.GetSnapshot());
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private sealed record EvaluationInput(RuntimeCutAcceptance Acceptance, CurrentSnapshot Snapshot);

    public sealed class MutableClock : IWallClock, IMonotonicClock
    {
        private DateTimeOffset now;

        public MutableClock(DateTimeOffset now)
        {
            this.now = now;
        }

        public DateTimeOffset GetUtcNow() => now;

        public MonotonicTimestamp GetTimestamp() => new(now.UtcTicks);

        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) =>
            TimeSpan.FromTicks(checked(finish.Value - start.Value));

        public void Advance(TimeSpan duration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
            now = now.Add(duration);
        }
    }
}
