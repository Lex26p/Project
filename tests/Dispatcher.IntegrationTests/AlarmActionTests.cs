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
public sealed class AlarmActionTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 20, 16, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public AlarmActionTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task AuthorizedActionsKeepFacetsIndependentAndAuditIdempotentReplay()
    {
        await using var context = await ActionTestContext.CreateAsync(cluster);
        var occurrence = await context.RaiseAsync();
        await context.Events.AcceptAlarmOccurrenceAsync(occurrence);
        var coordinator = context.Coordinator(context.Actions, new UnrestrictedAlarmMaintenanceConstraintResolver());

        var denied = await coordinator.AcknowledgeAsync(
            context.ReadOnlySession,
            context.ScopeId,
            context.PointId,
            occurrence.OccurrenceId,
            StateVersion.Initial,
            "denied");
        Assert.Equal("permission.denied", denied.Error?.Code.Value);

        var blocked = await context.Coordinator(context.Actions, new BlockingConstraints()).AssignAsync(
            context.ActionSession,
            context.ScopeId,
            context.PointId,
            occurrence.OccurrenceId,
            Guid.Parse("f7000000-0000-7000-8000-000000000001"),
            StateVersion.Initial,
            "blocked");
        Assert.Equal("alarm.maintenance_blocked", blocked.Error?.Code.Value);

        var acknowledged = await coordinator.AcknowledgeAsync(
            context.ActionSession,
            context.ScopeId,
            context.PointId,
            occurrence.OccurrenceId,
            StateVersion.Initial,
            "ack-1");
        var replay = await coordinator.AcknowledgeAsync(
            context.ActionSession,
            context.ScopeId,
            context.PointId,
            occurrence.OccurrenceId,
            StateVersion.Initial,
            "ack-1");
        var assigned = await coordinator.AssignAsync(
            context.ActionSession,
            context.ScopeId,
            context.PointId,
            occurrence.OccurrenceId,
            Guid.Parse("f7000000-0000-7000-8000-000000000001"),
            StateVersion.Initial,
            "assign-1");
        var shelved = await coordinator.ShelveAsync(
            context.ActionSession,
            context.ScopeId,
            context.PointId,
            occurrence.OccurrenceId,
            Start.AddMinutes(30),
            "planned maintenance",
            StateVersion.Initial,
            "shelve-1");

        Assert.Equal(AlarmActionCompletion.Applied, acknowledged.Value.Completion);
        Assert.Equal(AlarmActionCompletion.Replayed, replay.Value.Completion);
        Assert.Equal(AlarmActionCompletion.Applied, assigned.Value.Completion);
        Assert.Equal(AlarmActionCompletion.Applied, shelved.Value.Completion);
        var final = Assert.Single(await context.Alarm.ReadOccurrencesAsync(context.ScopeId));
        Assert.Equal(StateVersion.Initial, final.Condition.Version);
        Assert.Equal(StateVersion.From(2), final.Acknowledgement.Version);
        Assert.Equal(StateVersion.From(2), final.Assignment.Version);
        Assert.Equal(StateVersion.From(2), final.Shelving.Version);
        Assert.Equal(StateVersion.Initial, final.Suppression.Version);
        Assert.Equal(3, await context.Actions.CountAuditAsync(context.ScopeId));

        var events = await context.Events.QueryAsync(new EventQueryRequest(
            context.ScopeId,
            new HashSet<PointId> { context.PointId },
            new HashSet<OperationalEventKind>(),
            null,
            null,
            10,
            Priorities: new HashSet<AlarmPriority> { AlarmPriority.Critical }));
        Assert.Equal(AlarmPriority.Critical, Assert.Single(events.Value.Events).Priority);
        var projection = await context.Events.ReadProjectionSnapshotAsync(
            context.ScopeId,
            new HashSet<PointId> { context.PointId });
        var projected = Assert.Single(projection.Occurrences).Occurrence;
        Assert.Equal(StateVersion.From(2), projected.Acknowledgement.Version);
        Assert.Equal(StateVersion.From(2), projected.Assignment.Version);
        Assert.Equal(StateVersion.From(2), projected.Shelving.Version);
        var links = AlarmSourceLinkBuilder.From(context.PointId);
        Assert.Equal($"point:{context.PointId.Value:N}", links.DashboardBindingKey);
        Assert.True(RegistryRoutes.IsCanonical(links.EquipmentHref));
    }

    [Fact]
    public async Task ExpectedVersionRaceAllowsOneAssignment()
    {
        await using var context = await ActionTestContext.CreateAsync(cluster);
        var occurrence = await context.RaiseAsync();
        await context.Events.AcceptAlarmOccurrenceAsync(occurrence);
        var coordinator = context.Coordinator(context.Actions, new UnrestrictedAlarmMaintenanceConstraintResolver());

        var first = coordinator.AssignAsync(
            context.ActionSession,
            context.ScopeId,
            context.PointId,
            occurrence.OccurrenceId,
            Guid.Parse("f7000000-0000-7000-8000-000000000010"),
            StateVersion.Initial,
            "race-a");
        var second = coordinator.AssignAsync(
            context.ActionSession,
            context.ScopeId,
            context.PointId,
            occurrence.OccurrenceId,
            Guid.Parse("f7000000-0000-7000-8000-000000000011"),
            StateVersion.Initial,
            "race-b");
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(results, result => result.Error?.Code.Value == "alarm.expected_version");
        Assert.Equal(1, await context.Actions.CountAuditAsync(context.ScopeId));
    }

    [Fact]
    public async Task TimeoutRemainsUnknownUntilIdempotentReconciliation()
    {
        await using var context = await ActionTestContext.CreateAsync(cluster);
        var occurrence = await context.RaiseAsync();
        await context.Events.AcceptAlarmOccurrenceAsync(occurrence);
        var timingOutStore = new AlarmActionStore(
            context.DataSource,
            PostgreSqlClusterFixture.OwnerBRole,
            context.Clock,
            new AlarmActionCommitHook(_ => throw new TimeoutException("simulated response timeout")));
        var first = await context.Coordinator(
            timingOutStore,
            new UnrestrictedAlarmMaintenanceConstraintResolver()).AcknowledgeAsync(
                context.ActionSession,
                context.ScopeId,
                context.PointId,
                occurrence.OccurrenceId,
                StateVersion.Initial,
                "timeout-ack");
        Assert.Equal(AlarmActionCompletion.Unknown, first.Value.Completion);
        Assert.Null(first.Value.Occurrence);

        var reconciled = await context.Coordinator(
            context.Actions,
            new UnrestrictedAlarmMaintenanceConstraintResolver()).AcknowledgeAsync(
                context.ActionSession,
                context.ScopeId,
                context.PointId,
                occurrence.OccurrenceId,
                StateVersion.Initial,
                "timeout-ack");
        Assert.Equal(AlarmActionCompletion.Replayed, reconciled.Value.Completion);
        Assert.Equal(AlarmAcknowledgementState.Acknowledged, reconciled.Value.Occurrence!.Acknowledgement.State);
        Assert.Equal(1, await context.Actions.CountAuditAsync(context.ScopeId));
    }

    [Fact]
    public async Task AlarmFloodPreservesJournalAndPriorityFiltering()
    {
        await using var context = await ActionTestContext.CreateAsync(cluster);
        const int count = 32;
        var points = new HashSet<PointId>();
        for (var index = 0; index < count; index++)
        {
            var pointId = Dispatcher.Semantics.PointId.From(Guid.CreateVersion7());
            points.Add(pointId);
            await context.Events.AcceptAlarmOccurrenceAsync(context.Snapshot(
                AlarmOccurrenceId.New(),
                pointId,
                index % 2 == 0 ? AlarmPriority.Critical : AlarmPriority.Low));
        }

        var all = await context.Events.QueryAsync(new EventQueryRequest(
            context.ScopeId,
            points,
            new HashSet<OperationalEventKind>(),
            null,
            null,
            count));
        var critical = await context.Events.QueryAsync(new EventQueryRequest(
            context.ScopeId,
            points,
            new HashSet<OperationalEventKind>(),
            null,
            null,
            count,
            Priorities: new HashSet<AlarmPriority> { AlarmPriority.Critical }));
        Assert.Equal(count, all.Value.Events.Count);
        Assert.Equal(count / 2, critical.Value.Events.Count);
        Assert.All(critical.Value.Events, item => Assert.Equal(AlarmPriority.Critical, item.Priority));
        Assert.Equal(Enumerable.Range(1, count).Select(value => (ulong)value), all.Value.Events.Select(item => item.Position.Value));
    }

    private sealed class ActionTestContext : IAsyncDisposable
    {
        private static readonly SourceId SourceId = Dispatcher.Core.SourceId.From(
            Guid.Parse("f2000000-0000-7000-8000-000000000001"));
        private static readonly AlarmDefinitionId DefinitionId = AlarmDefinitionId.From(
            Guid.Parse("f4000000-0000-7000-8000-000000000001"));

        private ActionTestContext(TestDatabase database, NpgsqlDataSource dataSource, FixedClock clock)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ScopeId = RuntimeScopeId.From(Guid.Parse("f1000000-0000-7000-8000-000000000001"));
            PointId = Dispatcher.Semantics.PointId.From(Guid.Parse("f3000000-0000-7000-8000-000000000001"));
            Alarm = new AlarmStore(DataSource, PostgreSqlClusterFixture.OwnerBRole, Clock);
            Actions = new AlarmActionStore(DataSource, PostgreSqlClusterFixture.OwnerBRole, Clock);
            Events = new EventStore(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock,
                new EventDispatcherLimits(maxPageSize: 64, retainedProjectionChanges: 64, maxFeedChanges: 64));
            ActionSession = Session([
                AlarmPermissions.Acknowledge,
                AlarmPermissions.Assign,
                AlarmPermissions.Shelve,
                RuntimePermissions.ReadPoint(PointId),
            ]);
            ReadOnlySession = Session([RuntimePermissions.ReadPoint(PointId)]);
        }

        public TestDatabase Database { get; }

        public NpgsqlDataSource DataSource { get; }

        public FixedClock Clock { get; }

        public RuntimeScopeId ScopeId { get; }

        public PointId PointId { get; }

        public AlarmStore Alarm { get; }

        public AlarmActionStore Actions { get; }

        public EventStore Events { get; }

        public SessionSnapshot ActionSession { get; }

        public SessionSnapshot ReadOnlySession { get; }

        public static async Task<ActionTestContext> CreateAsync(PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                AlarmMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                EventMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
            return new ActionTestContext(database, dataSource, new FixedClock(Start));
        }

        public async Task<AlarmOccurrenceSnapshot> RaiseAsync()
        {
            var definition = new AlarmDefinition(
                DefinitionId,
                PointId,
                "Critical power",
                AlarmThresholdDirection.High,
                100,
                5,
                TimeSpan.Zero,
                TimeSpan.Zero,
                priority: AlarmPriority.Critical);
            Assert.True((await Alarm.ActivateDefinitionSetAsync(new AlarmDefinitionSet(
                ScopeId,
                RevisionNumber.Initial,
                [definition]))).IsSuccess);
            var runtime = new CoreRuntime(
                ScopeId,
                Clock,
                Clock,
                new RuntimeCurrentLimits(maxPoints: 4, retainedChangeCapacity: 16));
            var binding = new SourceBinding(
                ScopeId,
                SourceId,
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1));
            Assert.True(runtime.ActivateBinding(binding).IsSuccess);
            var observation = new SourceObservation(
                ScopeId,
                SourceId,
                PointId,
                new OwnerPosition<SourceObservation>(1),
                TypedValue.From(110L),
                Unit.FromSymbol("kW"),
                DataQuality.Good,
                Freshness.Fresh,
                SourceTimestamp.FromUtc(Start));
            var acceptance = runtime.Apply(RuntimeCut.Normalize(binding, 1, [observation]).Value).Value;
            var evaluated = await new AlarmEvaluator(ScopeId, RevisionNumber.Initial, Alarm)
                .EvaluatePostRuntimeCutAsync(acceptance, runtime.GetSnapshot());
            return Assert.Single(evaluated.Value.Occurrences);
        }

        public AlarmOccurrenceSnapshot Snapshot(
            AlarmOccurrenceId occurrenceId,
            PointId pointId,
            AlarmPriority priority) => new(
            occurrenceId,
            ScopeId,
            RevisionNumber.Initial,
            DefinitionId,
            pointId,
            priority,
            Start,
            null,
            new AlarmConditionFacet(AlarmConditionState.Active, null, Start, null, StateVersion.Initial),
            new AlarmAcknowledgementFacet(
                AlarmAcknowledgementState.Unacknowledged,
                null,
                null,
                StateVersion.Initial),
            new AlarmAssignmentFacet(null, null, StateVersion.Initial),
            new AlarmShelvingFacet(null, null, StateVersion.Initial),
            new AlarmSuppressionFacet(false, null, StateVersion.Initial));

        public AlarmActionCoordinator Coordinator(
            AlarmActionStore store,
            IAlarmMaintenanceConstraintResolver constraints) => new(store, Events, constraints, Clock);

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }

        private static SessionSnapshot Session(IReadOnlyCollection<PermissionCode> permissions) => new(
            SessionId.New(),
            SubjectId.New(),
            PrincipalKind.User,
            Start.AddMinutes(-1),
            Start.AddHours(1),
            new EffectivePermissions(permissions));
    }

    private sealed class BlockingConstraints : IAlarmMaintenanceConstraintResolver
    {
        public AlarmMaintenanceConstraintSnapshot Resolve(AlarmOccurrenceId occurrenceId) => new(
            occurrenceId,
            StateVersion.Initial,
            acknowledgementAllowed: true,
            assignmentAllowed: false,
            shelvingAllowed: false,
            windowEndsAt: Start.AddMinutes(10));
    }

    public sealed class FixedClock : IWallClock, IMonotonicClock
    {
        private readonly DateTimeOffset now;

        public FixedClock(DateTimeOffset now)
        {
            this.now = now;
        }

        public DateTimeOffset GetUtcNow() => now;

        public MonotonicTimestamp GetTimestamp() => new(now.UtcTicks);

        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) =>
            TimeSpan.FromTicks(checked(finish.Value - start.Value));
    }
}
