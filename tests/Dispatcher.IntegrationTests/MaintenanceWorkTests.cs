using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Equipment;
using Dispatcher.Events;
using Dispatcher.Facilities;
using Dispatcher.Maintenance;
using Dispatcher.MyWork;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class MaintenanceWorkTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 16, 0, 0, TimeSpan.Zero);
    private static readonly FacilityScopeId FacilityScopeId = Dispatcher.Facilities.FacilityScopeId.From(
        Guid.Parse("fd000000-0000-0000-0000-000000000001"));
    private static readonly RuntimeScopeId RuntimeScopeId = Dispatcher.Core.RuntimeScopeId.From(
        Guid.Parse("fd000000-0000-0000-0000-000000000002"));
    private static readonly SourceId SourceId = Dispatcher.Core.SourceId.From(
        Guid.Parse("fd000000-0000-0000-0000-000000000003"));
    private static readonly PointId PointId = Dispatcher.Semantics.PointId.From(
        Guid.Parse("fd000000-0000-0000-0000-000000000004"));
    private static readonly PersonId Assignee = PersonId.From(
        Guid.Parse("fd000000-0000-0000-0000-000000000005"));
    private readonly PostgreSqlClusterFixture cluster;

    public MaintenanceWorkTests(PostgreSqlClusterFixture cluster) => this.cluster = cluster;

    [Fact]
    public async Task ApprovedLifecycleChecklistEventLinkAndMyWorkRemainIndependentFromAlarm()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, AlarmMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, EventMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, EquipmentMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, MaintenanceMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, MyWorkMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        var clock = new FixedClock();
        var alarmStore = new AlarmStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        Assert.True((await alarmStore.ActivateDefinitionSetAsync(new AlarmDefinitionSet(
            RuntimeScopeId,
            RevisionNumber.Initial,
            [new AlarmDefinition(
                AlarmDefinitionId.New(), PointId, "High value", AlarmThresholdDirection.High,
                10, 0, TimeSpan.Zero, TimeSpan.Zero, priority: AlarmPriority.High)]))).IsSuccess);
        var occurrence = await RaiseAlarmAsync(alarmStore, clock);
        var eventStore = new EventStore(
            dataSource, PostgreSqlClusterFixture.OwnerARole, clock, new EventDispatcherLimits(100, 100, 50));
        var operationalEvent = (await eventStore.AcceptAlarmOccurrenceAsync(occurrence)).Value.Event;

        var equipmentStore = new EquipmentStore(dataSource, PostgreSqlClusterFixture.OwnerARole, clock);
        var assetStore = new MaintenanceStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        var assetService = new MaintenanceService(assetStore, equipmentStore, clock);
        var assetId = MaintenanceAssetId.New();
        var asset = (await assetService.CreateAssetAsync(
            Session(MaintenancePermissions.ManageAsset(FacilityScopeId)),
            new CreateMaintenanceAssetRequest(
                assetId, FacilityScopeId, "A-200", "Pump", "s31-asset-create"))).Value.Asset;
        var workStore = new MaintenanceWorkStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        var work = new MaintenanceWorkService(workStore, assetStore, clock);
        var manage = Session(
            MaintenanceWorkPermissions.Manage(FacilityScopeId),
            PermissionCode.From("events.dispatcher.read"),
            PermissionCode.From($"runtime.point.p{PointId.Value:N}.read"));
        var requestId = MaintenanceRequestId.New();
        var deniedEventRequest = await work.CreateRequestFromEventAsync(
            Session(MaintenanceWorkPermissions.Manage(FacilityScopeId)),
            new CreateMaintenanceRequestFromEvent(
                requestId, asset.AssetId, "Inspect alarm source", operationalEvent, "s31-event-denied"));
        Assert.Equal("permission.denied", deniedEventRequest.Error?.Code.Value);
        var request = (await work.CreateRequestFromEventAsync(
            manage,
            new CreateMaintenanceRequestFromEvent(
                requestId, asset.AssetId, "Inspect alarm source", operationalEvent, "s31-event-request"))).Value.Value;
        Assert.Equal(MaintenanceRequestState.Submitted, request.State);
        var approved = (await work.ApproveRequestAsync(
            manage,
            new ApproveMaintenanceRequest(requestId, request.Version, "s31-request-approve"))).Value.Value;
        Assert.Equal(MaintenanceRequestState.Approved, approved.State);

        var defectId = MaintenanceDefectId.New();
        var defect = (await work.CreateDefectAsync(
            manage,
            new CreateMaintenanceDefect(defectId, assetId, "Seal defect", "s31-defect-create"))).Value.Value;
        var confirmed = (await work.ConfirmDefectAsync(
            manage,
            new ConfirmMaintenanceDefect(defectId, defect.Version, "s31-defect-confirm"))).Value.Value;
        Assert.Equal(MaintenanceDefectState.Confirmed, confirmed.State);
        var defectWorkOrder = (await work.CreateWorkOrderAsync(
            manage,
            new CreateWorkOrderFromDefect(
                MaintenanceWorkOrderId.New(), defectId, "Replace seal", Assignee,
                new WorkOrderSafetyFields(false, false, null, null), [],
                confirmed.Version, "s31-defect-work-order"))).Value.Value;
        Assert.Equal(MaintenanceWorkSourceKind.Defect, defectWorkOrder.SourceKind);
        Assert.Equal(MaintenanceDefectState.Converted, (await workStore.ReadDefectAsync(defectId))!.State);

        var mandatoryItemId = WorkOrderChecklistItemId.New();
        var optionalItemId = WorkOrderChecklistItemId.New();
        var workOrderId = MaintenanceWorkOrderId.New();
        var workOrder = (await work.CreateWorkOrderAsync(
            manage,
            new CreateWorkOrderFromRequest(
                workOrderId, requestId, "Inspect and repair", Assignee,
                new WorkOrderSafetyFields(true, true, "Lock out equipment", null),
                [
                    new NewWorkOrderChecklistItem(mandatoryItemId, "Verify isolation", true),
                    new NewWorkOrderChecklistItem(optionalItemId, "Attach photo", false),
                ],
                approved.Version, "s31-request-work-order"))).Value.Value;
        Assert.Equal(MaintenanceRequestState.Converted, (await workStore.ReadRequestAsync(requestId))!.State);
        var executor = new MaintenanceWorkUserContext(
            Session(MaintenanceWorkPermissions.Execute(FacilityScopeId)), Assignee);
        var safetyDenied = await work.StartWorkOrderAsync(
            executor,
            new TransitionMaintenanceWorkOrder(
                workOrderId, workOrder.Version, "s31-start-denied", SafetyAcknowledged: false));
        Assert.Equal("maintenance.safety_acknowledgement", safetyDenied.Error?.Code.Value);
        var started = (await work.StartWorkOrderAsync(
            executor,
            new TransitionMaintenanceWorkOrder(
                workOrderId, workOrder.Version, "s31-start", SafetyAcknowledged: true))).Value.Value;
        Assert.Equal(MaintenanceWorkOrderState.InProgress, started.State);
        Assert.NotNull(started.Safety.AcknowledgedAt);
        var completed = (await work.CompleteWorkOrderAsync(
            executor,
            new TransitionMaintenanceWorkOrder(
                workOrderId, started.Version, "s31-complete"))).Value.Value;
        Assert.Equal(MaintenanceWorkOrderState.Completed, completed.State);
        var acceptor = Session(MaintenanceWorkPermissions.Accept(FacilityScopeId));
        var checklistDenied = await work.AcceptWorkOrderAsync(
            acceptor,
            new TransitionMaintenanceWorkOrder(
                workOrderId, completed.Version, "s31-accept-denied"));
        Assert.Equal("maintenance.checklist_incomplete", checklistDenied.Error?.Code.Value);
        var checkedOrder = (await work.CompleteChecklistItemAsync(
            executor,
            new CompleteWorkOrderChecklistItem(
                workOrderId, mandatoryItemId, completed.Version, "s31-checklist"))).Value.Value;
        var accepted = (await work.AcceptWorkOrderAsync(
            acceptor,
            new TransitionMaintenanceWorkOrder(
                workOrderId, checkedOrder.Version, "s31-accept"))).Value.Value;
        Assert.Equal(MaintenanceWorkOrderState.Accepted, accepted.State);
        Assert.Contains(accepted.Checklist, item => item.ItemId == optionalItemId && item.CompletedAt is null);

        var myWork = new MyWorkService(
            new MyWorkStore(dataSource, PostgreSqlClusterFixture.OwnerARole), clock);
        Assert.True((await myWork.AcceptSourceAssignmentAsync(
            WorkAssignmentProjection.FromMaintenanceWorkOrder(accepted))).IsSuccess);
        var visible = await myWork.ReadAsync(new MyWorkUserContext(
            Session(MyWorkPermissions.Read, MaintenancePermissions.Read(FacilityScopeId)), Assignee));
        Assert.Equal(workOrderId.Value, Assert.Single(visible.Value).SourceItemId);
        Assert.Empty((await myWork.ReadAsync(new MyWorkUserContext(
            Session(MyWorkPermissions.Read), Assignee))).Value);

        var deniedSource = await work.OpenRequestSourceAsync(
            Session(MaintenancePermissions.Read(FacilityScopeId)), requestId);
        Assert.Equal("permission.denied", deniedSource.Error?.Code.Value);
        var source = await work.OpenRequestSourceAsync(
            Session(
                MaintenancePermissions.Read(FacilityScopeId),
                PermissionCode.From("events.dispatcher.read"),
                PermissionCode.From($"runtime.point.p{PointId.Value:N}.read")),
            requestId);
        Assert.Equal(operationalEvent.EventId, source.Value.EventId);

        Assert.Equal(5, await workStore.CountAuditAsync(workOrderId.Value));
        Assert.Equal(3, await workStore.CountAuditAsync(requestId.Value));
        Assert.Equal(3, await workStore.CountAuditAsync(defectId.Value));
        Assert.Equal(
            AlarmAcknowledgementState.Unacknowledged,
            Assert.Single(await alarmStore.ReadOccurrencesAsync(RuntimeScopeId)).Acknowledgement.State);
        Assert.Equal(
            ["Assigned", "InProgress", "Completed", "Accepted"],
            Enum.GetNames<MaintenanceWorkOrderState>());
    }

    private static async Task<AlarmOccurrenceSnapshot> RaiseAlarmAsync(AlarmStore store, FixedClock clock)
    {
        var binding = new SourceBinding(
            RuntimeScopeId, SourceId, SourceBindingGeneration.From(1), SourceSessionGeneration.From(1));
        var runtime = new CoreRuntime(RuntimeScopeId, clock, clock, new RuntimeCurrentLimits(4, 8));
        Assert.True(runtime.ActivateBinding(binding).IsSuccess);
        var observation = new SourceObservation(
            RuntimeScopeId, SourceId, PointId, new OwnerPosition<SourceObservation>(1),
            TypedValue.From(42L), Unit.FromSymbol("kW"), DataQuality.Good, Freshness.Fresh,
            SourceTimestamp.FromUtc(Now));
        var acceptance = runtime.Apply(RuntimeCut.Normalize(binding, 1, [observation]).Value).Value;
        var evaluated = await new AlarmEvaluator(RuntimeScopeId, RevisionNumber.Initial, store)
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
