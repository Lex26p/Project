using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Server;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class EventDispatcherTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public EventDispatcherTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task AcknowledgementProjectionDoesNotMutateImmutableEvent()
    {
        await using var context = await EventTestContext.CreateAsync(cluster);
        var active = context.Occurrence(context.AllowedPointId, context.AllowedOccurrenceId);

        var accepted = await context.Store.AcceptAlarmOccurrenceAsync(active);
        var replayed = await context.Store.AcceptAlarmOccurrenceAsync(active);
        var acknowledged = active with
        {
            Acknowledgement = new AlarmAcknowledgementFacet(
                AlarmAcknowledgementState.Acknowledged,
                Guid.Parse("e6000000-0000-7000-8000-000000000001"),
                Start.AddSeconds(1),
                StateVersion.From(2)),
        };
        var projected = await context.Store.AcceptAlarmOccurrenceAsync(acknowledged);

        Assert.True(accepted.Value.EventInserted);
        Assert.True(accepted.Value.ProjectionChanged);
        Assert.False(replayed.Value.EventInserted);
        Assert.False(replayed.Value.ProjectionChanged);
        Assert.False(projected.Value.EventInserted);
        Assert.True(projected.Value.ProjectionChanged);
        Assert.Equal(accepted.Value.Event.EventId, projected.Value.Event.EventId);
        Assert.Equal((ulong)1, accepted.Value.Event.Position.Value);
        Assert.Equal((ulong)2, projected.Value.Projection.Version.Value);
        var snapshot = await context.Store.ReadProjectionSnapshotAsync(
            context.ScopeId,
            new HashSet<PointId> { context.AllowedPointId });
        Assert.Equal(
            AlarmAcknowledgementState.Acknowledged,
            Assert.Single(snapshot.Occurrences).Occurrence.Acknowledgement.State);

        var pendingClear = acknowledged with
        {
            Condition = new AlarmConditionFacet(
                AlarmConditionState.PendingClear,
                Start.AddSeconds(2),
                Start,
                null,
                StateVersion.From(2)),
        };
        var conditionTransition = await context.Store.AcceptAlarmOccurrenceAsync(pendingClear);
        Assert.True(conditionTransition.Value.EventInserted);
        Assert.Equal((ulong)2, conditionTransition.Value.Event.Position.Value);
        Assert.Equal((ulong)3, conditionTransition.Value.Projection.Version.Value);
        var filtered = await context.Store.QueryAsync(new EventQueryRequest(
            context.ScopeId,
            new HashSet<PointId> { context.AllowedPointId },
            new HashSet<OperationalEventKind> { OperationalEventKind.AlarmPendingClear },
            Start.AddSeconds(1),
            Start.AddSeconds(3),
            10));
        Assert.Equal(OperationalEventKind.AlarmPendingClear, Assert.Single(filtered.Value.Events).Kind);

        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var setRole = new NpgsqlCommand(
            $"SET LOCAL ROLE \"{PostgreSqlClusterFixture.OwnerBRole}\";",
            connection,
            transaction);
        await setRole.ExecuteNonQueryAsync();
        await using var mutation = new NpgsqlCommand(
            $"UPDATE {EventMigrations.Schema}.journal_event SET kind = 3 WHERE scope_id = @scope_id;",
            connection,
            transaction);
        mutation.Parameters.AddWithValue("scope_id", context.ScopeId.Value);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => mutation.ExecuteNonQueryAsync());
        Assert.Contains("immutable", exception.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthorizationFiltersRowsCountersAndRealtimeGapWithoutHiddenLeakage()
    {
        await using var context = await EventTestContext.CreateAsync(cluster);
        var allowed = context.Occurrence(context.AllowedPointId, context.AllowedOccurrenceId);
        var hidden = context.Occurrence(context.HiddenPointId, context.HiddenOccurrenceId);
        await context.Store.AcceptAlarmOccurrenceAsync(allowed);
        await context.Store.AcceptAlarmOccurrenceAsync(hidden);
        await context.Store.AcceptAlarmOccurrenceAsync(hidden with
        {
            Acknowledgement = new AlarmAcknowledgementFacet(
                AlarmAcknowledgementState.Acknowledged,
                Guid.Parse("e6000000-0000-7000-8000-000000000002"),
                Start.AddSeconds(1),
                StateVersion.From(2)),
        });
        var dispatcher = new AuthorizedEventDispatcher(context.Store, context.Clock);
        var request = new EventQueryRequest(
            context.ScopeId,
            new HashSet<PointId>(),
            new HashSet<OperationalEventKind>(),
            null,
            null,
            10);

        var page = await dispatcher.QueryAsync(context.AllowedSession, request, CancellationToken.None);
        var counters = await dispatcher.CountAsync(context.AllowedSession, request, CancellationToken.None);
        Assert.Equal(context.AllowedPointId, Assert.Single(page.Value.Events).PointId);
        Assert.Equal(1, counters.Value.EventCount);
        Assert.Equal(1, counters.Value.ActiveOccurrenceCount);
        Assert.Equal(1, counters.Value.UnacknowledgedOccurrenceCount);

        var noPermission = new SessionSnapshot(
            SessionId.New(),
            SubjectId.New(),
            PrincipalKind.User,
            Start.AddMinutes(-1),
            Start.AddHours(1),
            new EffectivePermissions([RuntimePermissions.ReadPoint(context.AllowedPointId)]));
        Assert.Equal(
            "permission.denied",
            (await dispatcher.QueryAsync(noPermission, request, CancellationToken.None)).Error?.Code.Value);

        var authorizedPoints = new HashSet<PointId> { context.AllowedPointId };
        var gap = await context.Store.ReadProjectionFeedAsync(
            context.ScopeId,
            cursor: 0,
            authorizedPoints);
        Assert.Equal(OccurrenceFeedKind.Gap, gap.Kind);
        Assert.Empty(gap.Changes);
        var hiddenOnlyCatchUp = await context.Store.ReadProjectionFeedAsync(
            context.ScopeId,
            cursor: 1,
            authorizedPoints);
        Assert.Equal(OccurrenceFeedKind.NoChange, hiddenOnlyCatchUp.Kind);
        Assert.Equal((ulong)3, hiddenOnlyCatchUp.To);
        Assert.Empty(hiddenOnlyCatchUp.Changes);
    }

    private sealed class EventTestContext : IAsyncDisposable
    {
        private static readonly AlarmDefinitionId DefinitionId = AlarmDefinitionId.From(
            Guid.Parse("e5000000-0000-7000-8000-000000000001"));

        private EventTestContext(TestDatabase database, NpgsqlDataSource dataSource, FixedClock clock)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ScopeId = RuntimeScopeId.From(Guid.Parse("e1000000-0000-7000-8000-000000000001"));
            AllowedPointId = PointId.From(Guid.Parse("e2000000-0000-7000-8000-000000000001"));
            HiddenPointId = PointId.From(Guid.Parse("e2000000-0000-7000-8000-000000000002"));
            AllowedOccurrenceId = AlarmOccurrenceId.From(
                Guid.Parse("e3000000-0000-7000-8000-000000000001"));
            HiddenOccurrenceId = AlarmOccurrenceId.From(
                Guid.Parse("e3000000-0000-7000-8000-000000000002"));
            Store = new EventStore(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock,
                new EventDispatcherLimits(maxPageSize: 32, retainedProjectionChanges: 2, maxFeedChanges: 2));
            AllowedSession = new SessionSnapshot(
                SessionId.New(),
                SubjectId.New(),
                PrincipalKind.User,
                Start.AddMinutes(-1),
                Start.AddHours(1),
                new EffectivePermissions([
                    EventPermissions.ReadDispatcher,
                    RuntimePermissions.ReadPoint(AllowedPointId),
                ]));
        }

        public TestDatabase Database { get; }

        public NpgsqlDataSource DataSource { get; }

        public FixedClock Clock { get; }

        public RuntimeScopeId ScopeId { get; }

        public PointId AllowedPointId { get; }

        public PointId HiddenPointId { get; }

        public AlarmOccurrenceId AllowedOccurrenceId { get; }

        public AlarmOccurrenceId HiddenOccurrenceId { get; }

        public EventStore Store { get; }

        public SessionSnapshot AllowedSession { get; }

        public static async Task<EventTestContext> CreateAsync(PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                EventMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
            return new EventTestContext(database, dataSource, new FixedClock(Start));
        }

        public AlarmOccurrenceSnapshot Occurrence(PointId pointId, AlarmOccurrenceId occurrenceId) => new(
            occurrenceId,
            ScopeId,
            RevisionNumber.Initial,
            DefinitionId,
            pointId,
            Start,
            null,
            new AlarmConditionFacet(
                AlarmConditionState.Active,
                null,
                Start,
                null,
                StateVersion.Initial),
            new AlarmAcknowledgementFacet(
                AlarmAcknowledgementState.Unacknowledged,
                null,
                null,
                StateVersion.Initial),
            new AlarmAssignmentFacet(null, null, StateVersion.Initial),
            new AlarmShelvingFacet(null, null, StateVersion.Initial),
            new AlarmSuppressionFacet(false, null, StateVersion.Initial));

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    public sealed class FixedClock : IWallClock
    {
        private readonly DateTimeOffset now;

        public FixedClock(DateTimeOffset now)
        {
            this.now = now;
        }

        public DateTimeOffset GetUtcNow() => now;
    }
}
