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
        var session = Session(clock, MaintenancePermissions.ManageAsset(ScopeId), MaintenanceWorkPermissions.Manage(ScopeId));
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
        Assert.Equal(MaintenanceRequestState.Submitted, (await work.ReadRequestAsync(requestId))!.State);
        Assert.Equal(sourceRequest.Version, (await work.ReadRequestAsync(requestId))!.Version);
        Assert.Equal(initialAssetVersion, (await assets.ReadAssetAsync(asset.AssetId))!.Version);
        Assert.Equal("Assigned>InProgress>Completed>Accepted", MaintenanceNucleusContract.Lifecycle);
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
