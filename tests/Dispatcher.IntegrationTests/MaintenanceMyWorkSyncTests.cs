using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Maintenance;
using Dispatcher.MyWork;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Server;
using Dispatcher.Workspace;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class MaintenanceMyWorkSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly FacilityScopeId ScopeId = FacilityScopeId.From(
        Guid.Parse("fa000000-0000-0000-0000-000000000001"));
    private static readonly SubjectId UserSubjectId = SubjectId.From(
        Guid.Parse("fa000000-0000-0000-0000-000000000002"));
    private static readonly PersonId TechnicianId = PersonId.From(
        Guid.Parse("fa000000-0000-0000-0000-000000000003"));
    private readonly PostgreSqlClusterFixture cluster;

    public MaintenanceMyWorkSyncTests(PostgreSqlClusterFixture cluster) => this.cluster = cluster;

    [Fact]
    public async Task VersionedCommandsSynchronizeRebuildableMyWorkProjection()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, WorkspaceMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, EquipmentMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, MaintenanceMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, MyWorkMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        await SeedWorkspaceAsync(dataSource);

        var clock = new FixedClock();
        var session = Session(
            MaintenancePermissions.Read(ScopeId),
            MaintenancePermissions.ManageAsset(ScopeId),
            MaintenanceWorkPermissions.Manage(ScopeId),
            MaintenanceWorkPermissions.Execute(ScopeId),
            MaintenanceWorkPermissions.Accept(ScopeId),
            MyWorkPermissions.Read);
        var assets = new MaintenanceStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        var assetService = new MaintenanceService(
            assets,
            new EquipmentStore(dataSource, PostgreSqlClusterFixture.OwnerARole, clock),
            clock);
        var asset = (await assetService.CreateAssetAsync(
            session,
            new CreateMaintenanceAssetRequest(
                MaintenanceAssetId.New(), ScopeId, "MW-1", "My Work asset", "mw-asset"))).Value.Asset;
        var workStore = new MaintenanceWorkStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        var work = new MaintenanceWorkService(workStore, assets, clock);
        var request = (await work.CreateRequestAsync(
            session,
            new CreateMaintenanceRequest(
                MaintenanceRequestId.New(), asset.AssetId, "Synchronized work", "mw-request"))).Value.Value;
        var approved = (await work.ApproveRequestAsync(
            session,
            new ApproveMaintenanceRequest(
                request.RequestId, request.Version, "mw-request-approve"))).Value.Value;
        var myWork = new MyWorkService(
            new MyWorkStore(dataSource, PostgreSqlClusterFixture.OwnerARole), clock);
        var application = new MaintenanceWorkApplicationService(
            work,
            myWork,
            new WorkspaceStore(dataSource, PostgreSqlClusterFixture.OwnerARole, clock),
            clock);
        var checklistItemId = WorkOrderChecklistItemId.New();
        var create = new CreateWorkOrderFromRequest(
            MaintenanceWorkOrderId.New(),
            approved.RequestId,
            "Execute synchronized work",
            TechnicianId,
            new WorkOrderSafetyFields(false, false, null, null),
            [new NewWorkOrderChecklistItem(checklistItemId, "Record result", true)],
            approved.Version,
            "mw-work-order");
        var created = (await application.CreateAsync(session, create, CancellationToken.None)).Value;
        var replay = (await application.CreateAsync(session, create, CancellationToken.None)).Value;
        Assert.Equal(MaintenanceWorkCommandDisposition.Applied, created.Disposition);
        Assert.Equal(MaintenanceWorkCommandDisposition.Replay, replay.Disposition);
        Assert.Equal("Assigned", (await ReadMyWorkAsync(myWork, session)).State);

        var accepted = (await application.AcceptAsync(
            session,
            Transition(created.Value, "mw-accept"),
            CancellationToken.None)).Value.Value;
        var started = (await application.StartAsync(
            session,
            Transition(accepted, "mw-start"),
            CancellationToken.None)).Value.Value;
        var checkedOrder = (await application.UpdateChecklistAsync(
            session,
            new UpdateWorkOrderChecklistItem(
                started.WorkOrderId,
                checklistItemId,
                true,
                started.Version,
                "mw-checklist"),
            CancellationToken.None)).Value.Value;
        var pending = (await application.SubmitAsync(
            session,
            Transition(checkedOrder, "mw-submit"),
            CancellationToken.None)).Value.Value;
        var completed = (await application.AcceptResultAsync(
            session,
            Transition(pending, "mw-complete"),
            CancellationToken.None)).Value.Value;

        var projection = await ReadMyWorkAsync(myWork, session);
        Assert.Equal(completed.WorkOrderId.Value, projection.SourceItemId);
        Assert.Equal(completed.Version, projection.SourceVersion);
        Assert.Equal(TechnicianId, projection.AssignedPersonId);
        Assert.Equal("Completed", projection.State);
        var persisted = await workStore.ReadWorkOrderAsync(completed.WorkOrderId);
        Assert.NotNull(persisted);
        Assert.Equal(completed.WorkOrderId, persisted.WorkOrderId);
        Assert.Equal(completed.Version, persisted.Version);
        Assert.Equal(completed.State, persisted.State);
        Assert.Equal(
            completed.Checklist.Select(item => (item.ItemId, item.CompletedAt)),
            persisted.Checklist.Select(item => (item.ItemId, item.CompletedAt)));
        Assert.True((await myWork.RebuildOwnerAsync("maintenance", [projection])).IsSuccess);
        Assert.Equal(completed.Version, (await ReadMyWorkAsync(myWork, session)).SourceVersion);
    }

    private static async Task<WorkAssignmentProjection> ReadMyWorkAsync(
        MyWorkService myWork,
        SessionSnapshot session) =>
        Assert.Single((await myWork.ReadAsync(
            new MyWorkUserContext(session, TechnicianId))).Value);

    private static TransitionMaintenanceWorkOrder Transition(
        MaintenanceWorkOrderSnapshot workOrder,
        string key) =>
        new(workOrder.WorkOrderId, workOrder.Version, key);

    private static SessionSnapshot Session(params PermissionCode[] permissions) => new(
        SessionId.New(), UserSubjectId, PrincipalKind.User, Now.AddMinutes(-1), Now.AddHours(1),
        new EffectivePermissions(permissions));

    private static async Task SeedWorkspaceAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            INSERT INTO {WorkspaceMigrations.Schema}.person
                (person_id, display_name, title, availability, visibility)
            VALUES ('{TechnicianId.Value}', 'Maintenance Technician', 'Technician', 1, 1);
            INSERT INTO {WorkspaceMigrations.Schema}.account
                (account_id, subject_id, person_id)
            VALUES ('fa000000-0000-0000-0000-000000000004', '{UserSubjectId.Value}', '{TechnicianId.Value}');
            """);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedClock : IWallClock
    {
        public DateTimeOffset GetUtcNow() => Now;
    }
}
