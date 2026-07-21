using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Incidents;
using Dispatcher.MyWork;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class IncidentMyWorkTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 8, 0, 0, TimeSpan.Zero);
    private static readonly RuntimeScopeId ScopeId = RuntimeScopeId.From(Guid.Parse("fb000000-0000-0000-0000-000000000001"));
    private static readonly SourceId SourceId = Dispatcher.Core.SourceId.From(Guid.Parse("fb000000-0000-0000-0000-000000000002"));
    private static readonly PointId PointId = Dispatcher.Semantics.PointId.From(Guid.Parse("fb000000-0000-0000-0000-000000000003"));
    private static readonly PersonId Coordinator = PersonId.From(Guid.Parse("fb000000-0000-0000-0000-000000000004"));
    private static readonly PersonId FirstAssignee = PersonId.From(Guid.Parse("fb000000-0000-0000-0000-000000000005"));
    private static readonly PersonId SecondAssignee = PersonId.From(Guid.Parse("fb000000-0000-0000-0000-000000000006"));
    private readonly PostgreSqlClusterFixture cluster;

    public IncidentMyWorkTests(PostgreSqlClusterFixture cluster) => this.cluster = cluster;

    [Fact]
    public async Task IncidentTaskAndRebuildableMyWorkRemainIndependentFromAlarmOwner()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(dataSource, AlarmMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        await PostgresMigrationRunner.ApplyAsync(dataSource, EventMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        await PostgresMigrationRunner.ApplyAsync(dataSource, IncidentMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        await PostgresMigrationRunner.ApplyAsync(dataSource, MyWorkMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        var clock = new FixedClock();
        var alarmStore = new AlarmStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        Assert.True((await alarmStore.ActivateDefinitionSetAsync(new AlarmDefinitionSet(
            ScopeId,
            RevisionNumber.Initial,
            [new AlarmDefinition(
                AlarmDefinitionId.New(), PointId, "High value", AlarmThresholdDirection.High,
                10, 0, TimeSpan.Zero, TimeSpan.Zero, priority: AlarmPriority.High)]))).IsSuccess);
        var occurrence = await RaiseAlarmAsync(alarmStore, clock);
        var eventStore = new EventStore(
            dataSource, PostgreSqlClusterFixture.OwnerARole, clock, new EventDispatcherLimits(100, 100, 50));
        var operationalEvent = (await eventStore.AcceptAlarmOccurrenceAsync(occurrence)).Value.Event;

        var incidentStore = new IncidentStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        var incidents = new IncidentService(incidentStore, clock);
        var incidentId = IncidentId.New();
        var commandSession = Session(
            IncidentPermissions.Create,
            IncidentPermissions.LinkSource,
            IncidentPermissions.ManageTask,
            IncidentPermissions.Read,
            PermissionCode.From("events.dispatcher.read"),
            PermissionCode.From($"runtime.point.p{PointId.Value:N}.read"));
        var createRequest = new CreateIncidentRequest(incidentId, "Investigate high value", Coordinator, "incident-create-1");
        var created = await incidents.CreateAsync(commandSession, createRequest);
        var replay = await incidents.CreateAsync(commandSession, createRequest);
        Assert.Equal(IncidentCommandDisposition.Applied, created.Value.Disposition);
        Assert.Equal(IncidentCommandDisposition.Replay, replay.Value.Disposition);
        Assert.Equal(created.Value.Value.IncidentId, replay.Value.Value.IncidentId);

        var deniedLink = await incidents.LinkEventAsync(
            Session(IncidentPermissions.LinkSource),
            new LinkIncidentEventRequest(incidentId, operationalEvent, StateVersion.Initial, "incident-link-denied"));
        Assert.Equal("permission.denied", deniedLink.Error?.Code.Value);
        var linked = await incidents.LinkEventAsync(
            commandSession,
            new LinkIncidentEventRequest(incidentId, operationalEvent, StateVersion.Initial, "incident-link-1"));
        Assert.Single(linked.Value.Value.SourceLinks);

        var taskId = IncidentTaskId.New();
        var task = (await incidents.CreateTaskAsync(
            commandSession,
            new CreateIncidentTaskRequest(
                taskId, incidentId, "Inspect source", FirstAssignee,
                linked.Value.Value.Version, "incident-task-create-1"))).Value.Value;
        var myWorkStore = new MyWorkStore(dataSource, PostgreSqlClusterFixture.OwnerARole);
        var myWork = new MyWorkService(myWorkStore, clock);
        Assert.True((await myWork.AcceptSourceAssignmentAsync(WorkAssignmentProjection.FromIncidentTask(task))).IsSuccess);
        var visible = await myWork.ReadAsync(new MyWorkUserContext(
            Session(MyWorkPermissions.Read, IncidentPermissions.Read), FirstAssignee));
        Assert.Equal(taskId.Value, Assert.Single(visible.Value).SourceItemId);
        var filtered = await myWork.ReadAsync(new MyWorkUserContext(
            Session(MyWorkPermissions.Read), FirstAssignee));
        Assert.Empty(filtered.Value);

        var firstContext = new IncidentUserContext(Session(IncidentPermissions.TransitionTask), FirstAssignee);
        var accepted = (await incidents.AcceptTaskAsync(
            firstContext,
            new TransitionIncidentTaskRequest(taskId, task.Version, "incident-task-accept-1"))).Value.Value;
        Assert.Equal(IncidentTaskState.Accepted, accepted.State);
        var transferred = (await incidents.TransferTaskAsync(
            firstContext,
            new TransitionIncidentTaskRequest(
                taskId, accepted.Version, "incident-task-transfer-1", SecondAssignee))).Value.Value;
        Assert.Equal(SecondAssignee, transferred.AssignedPersonId);
        Assert.True((await myWork.AcceptSourceAssignmentAsync(WorkAssignmentProjection.FromIncidentTask(transferred))).IsSuccess);
        Assert.Empty((await myWork.ReadAsync(new MyWorkUserContext(
            Session(MyWorkPermissions.Read, IncidentPermissions.Read), FirstAssignee))).Value);

        var secondContext = new IncidentUserContext(Session(IncidentPermissions.TransitionTask), SecondAssignee);
        var returned = (await incidents.ReturnTaskAsync(
            secondContext,
            new TransitionIncidentTaskRequest(taskId, transferred.Version, "incident-task-return-1"))).Value.Value;
        Assert.Equal(IncidentTaskState.Returned, returned.State);
        Assert.Equal(Coordinator, returned.AssignedPersonId);
        var finalProjection = WorkAssignmentProjection.FromIncidentTask(returned);
        Assert.True((await myWork.AcceptSourceAssignmentAsync(finalProjection)).IsSuccess);
        Assert.True((await myWork.RebuildOwnerAsync("incidents", [finalProjection])).IsSuccess);

        var restartedMyWork = new MyWorkService(
            new MyWorkStore(dataSource, PostgreSqlClusterFixture.OwnerARole), clock);
        var rebuilt = await restartedMyWork.ReadAsync(new MyWorkUserContext(
            Session(MyWorkPermissions.Read, IncidentPermissions.Read), Coordinator));
        Assert.Equal(returned.Version, Assert.Single(rebuilt.Value).SourceVersion);
        Assert.Equal(returned, await incidentStore.ReadTaskAsync(taskId));
        Assert.Equal(
            AlarmAcknowledgementState.Unacknowledged,
            Assert.Single(await alarmStore.ReadOccurrencesAsync(ScopeId)).Acknowledgement.State);
    }

    private static async Task<AlarmOccurrenceSnapshot> RaiseAlarmAsync(AlarmStore store, FixedClock clock)
    {
        var binding = new SourceBinding(
            ScopeId, SourceId, SourceBindingGeneration.From(1), SourceSessionGeneration.From(1));
        var runtime = new CoreRuntime(ScopeId, clock, clock, new RuntimeCurrentLimits(4, 8));
        Assert.True(runtime.ActivateBinding(binding).IsSuccess);
        var observation = new SourceObservation(
            ScopeId, SourceId, PointId, new OwnerPosition<SourceObservation>(1),
            TypedValue.From(42L), Unit.FromSymbol("kW"), DataQuality.Good, Freshness.Fresh,
            SourceTimestamp.FromUtc(Now));
        var acceptance = runtime.Apply(RuntimeCut.Normalize(binding, 1, [observation]).Value).Value;
        var evaluated = await new AlarmEvaluator(ScopeId, RevisionNumber.Initial, store)
            .EvaluatePostRuntimeCutAsync(acceptance, runtime.GetSnapshot());
        return Assert.Single(evaluated.Value.Occurrences);
    }

    private static SessionSnapshot Session(params PermissionCode[] permissions) => new(
        SessionId.New(), SubjectId.New(), PrincipalKind.User, Now.AddMinutes(-1), Now.AddHours(1),
        new EffectivePermissions(permissions));

    private sealed class FixedClock : IWallClock, IMonotonicClock
    {
        public DateTimeOffset GetUtcNow() => Now;
        public MonotonicTimestamp GetTimestamp() => new(0);
        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) => TimeSpan.Zero;
    }
}
