using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Maintenance;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class MaintenanceSchedulerTests
{
    private static readonly FacilityScopeId ScopeId = FacilityScopeId.From(
        Guid.Parse("fe000000-0000-0000-0000-000000000001"));
    private static readonly PersonId Assignee = PersonId.From(
        Guid.Parse("fe000000-0000-0000-0000-000000000002"));
    private readonly PostgreSqlClusterFixture cluster;

    public MaintenanceSchedulerTests(PostgreSqlClusterFixture cluster) => this.cluster = cluster;

    [Fact]
    public async Task MaterializationIsConcurrentRestartSafeAndKeepsSourceOwnersIndependent()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, EquipmentMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, MaintenanceMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        var clock = new MutableClock(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
        var equipment = new EquipmentStore(dataSource, PostgreSqlClusterFixture.OwnerARole, clock);
        var assets = new MaintenanceStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        var assetService = new MaintenanceService(assets, equipment, clock);
        var session = Session(
            clock,
            MaintenancePermissions.Read(ScopeId),
            MaintenancePermissions.ManageAsset(ScopeId),
            MaintenanceWorkPermissions.Manage(ScopeId));
        var asset = (await assetService.CreateAssetAsync(session, new CreateMaintenanceAssetRequest(
            MaintenanceAssetId.New(), ScopeId, "S32-A", "Scheduler asset", "s32-asset"))).Value.Asset;
        var initialAssetVersion = asset.Version;
        var authorization = SessionAuthorization.AuthorizeMutation(
            session, MaintenanceWorkPermissions.Manage(ScopeId), clock).Value;
        var work = new MaintenanceWorkStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        var requestId = MaintenanceRequestId.New();
        var sourceRequest = (await work.CreateRequestAsync(
            authorization, asset,
            new CreateMaintenanceRequest(requestId, asset.AssetId, "Independent request", "s32-request"))).Value.Value;

        var scheduler = new MaintenanceSchedulerStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        var plan = new ApprovedMaintenancePlan(
            MaintenancePlanId.New(), asset.AssetId, RevisionNumber.Initial, "Daily inspection",
            new DateOnly(2026, 6, 20), new MaintenanceRecurrence(1), new DateOnly(2026, 7, 22));
        var policy = new MaintenanceMaterializationPolicy(
            Assignee, new WorkOrderSafetyFields(false, false, "Visual inspection only", null),
            [new NewWorkOrderChecklistItem(WorkOrderChecklistItemId.New(), "Record result", true)]);
        var first = (await scheduler.EnsureForecastAsync(
            authorization, plan, asset, new DateOnly(2026, 7, 22), policy)).Value;
        var replay = (await scheduler.EnsureForecastAsync(
            authorization, plan, asset, new DateOnly(2026, 7, 22), policy)).Value;
        Assert.Equal(33, first.Count);
        Assert.Equal(first.Select(value => value.ObligationId), replay.Select(value => value.ObligationId));
        Assert.Equal(32, (await scheduler.ReadOverdueAsync(new DateOnly(2026, 7, 22))).Count);
        var planning = new MaintenancePlanningService(
            scheduler, clock, new MaintenanceQueryLimits(10, 60));
        var persistedPlan = (await planning.GetPlanAsync(session, plan.PlanId)).Value;
        Assert.Equal(plan.PlanId, persistedPlan.Plan.PlanId);
        Assert.Equal(plan.Recurrence.IntervalDays, persistedPlan.Plan.Recurrence.IntervalDays);
        var firstForecastPage = (await planning.QueryForecastAsync(
            session,
            new MaintenanceForecastQuery(
                ScopeId,
                new DateOnly(2026, 6, 20),
                new DateOnly(2026, 7, 22),
                10))).Value;
        Assert.Equal(10, firstForecastPage.Entries.Count);
        Assert.NotNull(firstForecastPage.NextDueOn);
        var secondCalendarPage = (await planning.QueryCalendarAsync(
            session,
            new MaintenanceForecastQuery(
                ScopeId,
                new DateOnly(2026, 6, 20),
                new DateOnly(2026, 7, 22),
                10,
                firstForecastPage.NextDueOn,
                firstForecastPage.NextObligationId))).Value;
        Assert.Equal(10, secondCalendarPage.Entries.Count);
        Assert.Empty(firstForecastPage.Entries.Select(value => value.ObligationId)
            .Intersect(secondCalendarPage.Entries.Select(value => value.ObligationId)));
        Assert.True((await planning.GetPlanAsync(
            Session(clock, MaintenanceWorkPermissions.Manage(ScopeId)),
            plan.PlanId)).IsFailure);

        var claims = await Task.WhenAll(
            scheduler.ClaimNextAsync("s32-worker-a", TimeSpan.FromMinutes(1)),
            scheduler.ClaimNextAsync("s32-worker-b", TimeSpan.FromMinutes(1)));
        var crashed = Assert.IsType<MaintenanceForecastObligation>(claims[0]);
        var concurrent = Assert.IsType<MaintenanceForecastObligation>(claims[1]);
        Assert.NotEqual(crashed.ObligationId, concurrent.ObligationId);

        var created = (await work.CreateWorkOrderAsync(authorization, ForecastCommand(crashed))).Value;
        Assert.Equal(MaintenanceWorkCommandDisposition.Applied, created.Disposition);
        clock.Advance(TimeSpan.FromMinutes(2));
        var recovered = (await new MaintenanceMaterializer(scheduler, work, clock).RunOnceAsync(
            session, "s32-recovery", TimeSpan.FromMinutes(1))).Value;
        Assert.Equal(crashed.ObligationId, recovered.Obligation!.ObligationId);
        Assert.Equal(crashed.WorkOrderId, recovered.WorkOrder!.WorkOrderId);
        Assert.Equal(MaintenanceWorkOrderState.Overdue, recovered.WorkOrder.State);

        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            "SELECT count(*) FROM maintenance.work_order WHERE source_kind = 3 AND source_id = @source;", connection))
        {
            command.Parameters.AddWithValue("source", crashed.ObligationId.Value);
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        }

        Assert.Equal(31, (await scheduler.ReadOverdueAsync(new DateOnly(2026, 7, 22))).Count);
        var timeline = await work.ReadTimelineAsync(crashed.WorkOrderId.Value);
        Assert.Equal("create-work-order-forecast", Assert.Single(timeline).Action);
        var link = MaintenanceCrossLinks.SourceFor(recovered.WorkOrder);
        Assert.Equal($"/maintenance/forecast/{crashed.ObligationId.Value}", link.Route);
        Assert.Equal(MaintenancePermissions.Read(ScopeId), Assert.Single(link.RequiredPermissions));
        var workService = new MaintenanceWorkService(work, assets, clock);
        var overdueOverview = (await workService.ReadOverviewAsync(session, ScopeId)).Value;
        Assert.Equal(1, overdueOverview.Overdue);
        Assert.Equal(1, overdueOverview.RequiresAssignment);
        Assert.Equal(0, overdueOverview.InProgress);
        Assert.Equal(0, overdueOverview.PendingAcceptance);
        var executor = new MaintenanceWorkUserContext(
            Session(clock, MaintenanceWorkPermissions.Execute(ScopeId)), Assignee);
        var claimed = (await workService.ClaimWorkOrderAsync(
            executor,
            new ClaimMaintenanceWorkOrder(
                recovered.WorkOrder.WorkOrderId,
                recovered.WorkOrder.Version,
                "s32-claim"))).Value.Value;
        Assert.Equal(MaintenanceWorkOrderState.Assigned, claimed.State);
        var accepted = (await workService.AcceptWorkOrderAsync(
            executor,
            new TransitionMaintenanceWorkOrder(
                claimed.WorkOrderId,
                claimed.Version,
                "s32-accept"))).Value.Value;
        Assert.Equal(MaintenanceWorkOrderState.Accepted, accepted.State);
        var started = (await workService.StartWorkOrderAsync(
            executor,
            new TransitionMaintenanceWorkOrder(
                accepted.WorkOrderId,
                accepted.Version,
                "s32-start",
                SafetyAcknowledged: true))).Value.Value;
        Assert.Equal(MaintenanceWorkOrderState.InProgress, started.State);
        var activeOverview = (await workService.ReadOverviewAsync(session, ScopeId)).Value;
        Assert.Equal(1, activeOverview.InProgress);
        Assert.Equal(0, activeOverview.RequiresAssignment);
        var submitDenied = await workService.SubmitWorkOrderForAcceptanceAsync(
            executor,
            new TransitionMaintenanceWorkOrder(
                started.WorkOrderId,
                started.Version,
                "s32-submit-denied"));
        Assert.Equal("maintenance.checklist_incomplete", submitDenied.Error?.Code.Value);
        var checklistUpdated = (await workService.UpdateChecklistItemAsync(
            executor,
            new UpdateWorkOrderChecklistItem(
                started.WorkOrderId,
                Assert.Single(started.Checklist).ItemId,
                true,
                started.Version,
                "s32-checklist"))).Value.Value;
        var pending = (await workService.SubmitWorkOrderForAcceptanceAsync(
            executor,
            new TransitionMaintenanceWorkOrder(
                checklistUpdated.WorkOrderId,
                checklistUpdated.Version,
                "s32-submit"))).Value.Value;
        Assert.Equal(MaintenanceWorkOrderState.PendingAcceptance, pending.State);
        var completed = (await workService.AcceptWorkResultAsync(
            Session(clock, MaintenanceWorkPermissions.Accept(ScopeId)),
            new TransitionMaintenanceWorkOrder(
                pending.WorkOrderId,
                pending.Version,
                "s32-accept-result"))).Value.Value;
        Assert.Equal(MaintenanceWorkOrderState.Completed, completed.State);
        var completedOverview = (await workService.ReadOverviewAsync(session, ScopeId)).Value;
        Assert.Equal(0, completedOverview.Overdue);
        Assert.Equal(0, completedOverview.InProgress);
        Assert.Equal(0, completedOverview.PendingAcceptance);
        var completedPage = (await workService.QueryWorkOrdersAsync(
            session,
            new MaintenanceWorkOrderQuery(
                ScopeId,
                10,
                MaintenanceWorkOrderState.Completed))).Value;
        Assert.Contains(completedPage.WorkOrders, item => item.WorkOrderId == completed.WorkOrderId);
        Assert.Equal(MaintenanceRequestState.Submitted, (await work.ReadRequestAsync(requestId))!.State);
        Assert.Equal(sourceRequest.Version, (await work.ReadRequestAsync(requestId))!.Version);
        Assert.Equal(initialAssetVersion, (await assets.ReadAssetAsync(asset.AssetId))!.Version);
        Assert.Equal(
            "Overdue>Assigned>Accepted>InProgress>PendingAcceptance>Completed",
            MaintenanceNucleusContract.Lifecycle);
        Assert.Equal(["Request", "Defect", "Forecast"], Enum.GetNames<MaintenanceWorkSourceKind>());
    }

    private static CreateWorkOrderFromForecast ForecastCommand(MaintenanceForecastObligation obligation) => new(
        obligation.WorkOrderId, obligation.ObligationId, obligation.AssetId, obligation.ScopeId,
        obligation.Title, obligation.Policy.AssignedPersonId, obligation.Policy.Safety,
        obligation.Policy.Checklist, $"forecast:{obligation.ObligationId.Value:N}");

    private static SessionSnapshot Session(MutableClock clock, params PermissionCode[] permissions) => new(
        SessionId.New(), SubjectId.New(), PrincipalKind.User, clock.GetUtcNow().AddMinutes(-1),
        clock.GetUtcNow().AddHours(1), new EffectivePermissions(permissions));

    private sealed class MutableClock(DateTimeOffset now) : IWallClock
    {
        private DateTimeOffset now = now;
        public DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
