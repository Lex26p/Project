using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Maintenance;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class MaintenanceAssetTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly FacilityScopeId ScopeId = FacilityScopeId.From(
        Guid.Parse("fc000000-0000-0000-0000-000000000001"));
    private static readonly LocationId LocationId = Dispatcher.Facilities.LocationId.From(
        Guid.Parse("fc000000-0000-0000-0000-000000000002"));
    private readonly PostgreSqlClusterFixture cluster;

    public MaintenanceAssetTests(PostgreSqlClusterFixture cluster) => this.cluster = cluster;

    [Fact]
    public async Task IndependentAssetLinkHistoryAndReadOnlyForecastRemainDistinctFromWorkOrder()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, EquipmentMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, MaintenanceMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        var clock = new FixedClock();
        var equipmentStore = new EquipmentStore(dataSource, PostgreSqlClusterFixture.OwnerARole, clock);
        var maintenanceStore = new MaintenanceStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        var maintenance = new MaintenanceService(maintenanceStore, equipmentStore, clock);
        var assetId = MaintenanceAssetId.New();
        var manager = Session(MaintenancePermissions.ManageAsset(ScopeId));
        var createRequest = new CreateMaintenanceAssetRequest(
            assetId, ScopeId, "A-100", "Independent pump asset", "maintenance-create-1");
        var created = await maintenance.CreateAssetAsync(manager, createRequest);
        var replay = await maintenance.CreateAssetAsync(manager, createRequest);
        Assert.Equal(MaintenanceCommandDisposition.Applied, created.Value.Disposition);
        Assert.Equal(MaintenanceCommandDisposition.Replay, replay.Value.Disposition);
        Assert.Null(created.Value.Asset.EquipmentId);
        Assert.Equal(assetId, replay.Value.Asset.AssetId);

        var updated = await maintenance.UpdateAssetAsync(
            manager,
            new UpdateMaintenanceAssetRequest(
                assetId, "A-100", "Independent pump", created.Value.Asset.Version, "maintenance-update-1"));
        Assert.Equal(assetId, updated.Value.Asset.AssetId);
        var equipmentId = EquipmentId.From(Guid.Parse("fc000000-0000-0000-0000-000000000003"));
        var equipmentAuthorization = SessionAuthorization.AuthorizeAccess(
            Session(EquipmentPermissions.Write(ScopeId)), EquipmentPermissions.Write(ScopeId), clock).Value;
        Assert.True((await equipmentStore.CreateEquipmentAsync(
            equipmentAuthorization,
            new CreateEquipment(equipmentId, ScopeId, LocationId, "EQ-100", "Telemetry equipment"))).IsSuccess);

        var deniedLink = await maintenance.LinkEquipmentAsync(
            manager,
            new LinkMaintenanceEquipmentRequest(
                assetId, equipmentId, updated.Value.Asset.Version, "maintenance-link-denied"));
        Assert.Equal("permission.denied", deniedLink.Error?.Code.Value);
        var linked = await maintenance.LinkEquipmentAsync(
            Session(MaintenancePermissions.ManageAsset(ScopeId), EquipmentPermissions.Read(ScopeId)),
            new LinkMaintenanceEquipmentRequest(
                assetId, equipmentId, updated.Value.Asset.Version, "maintenance-link-1"));
        Assert.Equal(assetId, linked.Value.Asset.AssetId);
        Assert.Equal(equipmentId, linked.Value.Asset.EquipmentId);
        var unlinked = await maintenance.UnlinkEquipmentAsync(
            manager,
            new UnlinkMaintenanceEquipmentRequest(
                assetId, linked.Value.Asset.Version, "maintenance-unlink-1"));
        Assert.Equal(assetId, unlinked.Value.Asset.AssetId);
        Assert.Null(unlinked.Value.Asset.EquipmentId);

        var history = await maintenance.ReadLinkHistoryAsync(
            Session(MaintenancePermissions.Read(ScopeId)), assetId);
        Assert.Collection(
            history.Value,
            item =>
            {
                Assert.Equal(MaintenanceEquipmentLinkAction.Linked, item.Action);
                Assert.Equal(equipmentId, item.EquipmentId);
            },
            item =>
            {
                Assert.Equal(MaintenanceEquipmentLinkAction.Unlinked, item.Action);
                Assert.Null(item.EquipmentId);
            });
        Assert.Equal(4, await maintenanceStore.CountAuditAsync(assetId));

        var restarted = new MaintenanceService(
            new MaintenanceStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock),
            new EquipmentStore(dataSource, PostgreSqlClusterFixture.OwnerARole, clock),
            clock);
        var assets = await restarted.ReadAssetsAsync(Session(MaintenancePermissions.Read(ScopeId)), ScopeId);
        var restoredAsset = Assert.Single(assets.Value);
        Assert.Equal(assetId, restoredAsset.AssetId);
        Assert.Null(restoredAsset.EquipmentId);
        var deniedRead = await restarted.ReadAssetsAsync(Session(), ScopeId);
        Assert.Equal("permission.denied", deniedRead.Error?.Code.Value);

        var plan = new ApprovedMaintenancePlan(
            MaintenancePlanId.New(), assetId, RevisionNumber.Initial, "Weekly inspection",
            new DateOnly(2026, 7, 1), new MaintenanceRecurrence(7));
        var forecast = MaintenancePlanCalendar.Query(
            [plan], assets.Value, new MaintenanceCalendarQuery(
                new DateOnly(2026, 7, 10), new DateOnly(2026, 7, 25)));
        Assert.Equal(
            [new DateOnly(2026, 7, 15), new DateOnly(2026, 7, 22)],
            forecast.Select(value => value.DueOn));
        Assert.All(forecast, value => Assert.Equal(plan.Revision, value.PlanRevision));
        Assert.Equal(
            [nameof(MaintenancePlanCalendar.Query)],
            typeof(MaintenancePlanCalendar).GetMethods()
                .Where(method => method.DeclaringType == typeof(MaintenancePlanCalendar))
                .Select(method => method.Name)
                .Distinct());
    }

    private static SessionSnapshot Session(params PermissionCode[] permissions) => new(
        SessionId.New(), SubjectId.New(), PrincipalKind.User, Now.AddMinutes(-1), Now.AddHours(1),
        new EffectivePermissions(permissions));

    private sealed class FixedClock : IWallClock
    {
        public DateTimeOffset GetUtcNow() => Now;
    }
}
