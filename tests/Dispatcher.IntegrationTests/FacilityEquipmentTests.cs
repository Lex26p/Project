using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class FacilityEquipmentTests
{
    private readonly PostgreSqlClusterFixture cluster;

    public FacilityEquipmentTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task PhysicalAndFunctionalRelationsRejectInvalidGraphsAndSurviveRestart()
    {
        await using var context = await FacilityEquipmentContext.CreateAsync(cluster);

        var relation = await context.Facilities.AddFunctionalRelationAsync(
            context.AllScopesSession,
            context.ScopeA,
            new AddFunctionalLocationRelation(context.AreaA, context.RoomA, 1));
        var crossScope = await context.Facilities.AddFunctionalRelationAsync(
            context.AllScopesSession,
            context.ScopeA,
            new AddFunctionalLocationRelation(context.AreaA, context.RootB, 2));
        var cycle = await context.Facilities.MoveLocationAsync(
            context.AllScopesSession,
            context.ScopeA,
            new MoveLocation(context.RootA, context.RoomA, 1));
        var stale = await context.Facilities.MoveLocationAsync(
            context.AllScopesSession,
            context.ScopeA,
            new MoveLocation(context.AreaA, null, 1));

        Assert.Equal(2, relation.Value.Version);
        Assert.Equal("facility.functional_scope", crossScope.Error?.Code.Value);
        Assert.Equal("facility.physical_cycle", cycle.Error?.Code.Value);
        Assert.Equal("facility.version_conflict", stale.Error?.Code.Value);

        var restarted = context.CreateFacilityService();
        var graph = await restarted.ReadGraphAsync(context.LimitedSession, context.ScopeA);
        Assert.True(graph.IsSuccess);
        Assert.Equal(3, graph.Value.Locations.Count);
        Assert.Equal(context.RootA, graph.Value.Locations.Single(item => item.LocationId == context.AreaA).PhysicalParentId);
        Assert.Equal(context.AreaA, graph.Value.Locations.Single(item => item.LocationId == context.RoomA).PhysicalParentId);
        Assert.Equal(
            new FunctionalLocationRelation(context.AreaA, context.RoomA),
            Assert.Single(graph.Value.FunctionalRelations));
        Assert.Equal(
            "permission.denied",
            (await restarted.ReadGraphAsync(context.LimitedSession, context.ScopeB)).Error?.Code.Value);
        await using var audit = context.DataSource.CreateCommand(
            $"SELECT count(*) FROM {FacilityMigrations.Schema}.mutation_audit;");
        Assert.Equal(5L, await audit.ExecuteScalarAsync());
    }

    [Fact]
    public async Task EquipmentOwnsStablePointDefinitionsAndUsesOptimisticConcurrency()
    {
        await using var context = await FacilityEquipmentContext.CreateAsync(cluster);
        var equipmentId = EquipmentId.From(Guid.Parse("63000000-0000-0000-0000-000000000001"));
        var pointId = PointId.From(Guid.Parse("64000000-0000-0000-0000-000000000001"));

        var created = await context.Equipment.CreateEquipmentAsync(
            context.LimitedSession,
            new CreateEquipment(equipmentId, context.ScopeA, context.RoomA, "AHU-1", "Air handler"));
        var point = await context.Equipment.AddPointAsync(
            context.LimitedSession,
            context.ScopeA,
            new AddPointDefinition(
                equipmentId,
                pointId,
                "SUPPLY-T",
                "Supply temperature",
                Unit.FromSymbol("°C"),
                created.Value.Version));
        var stalePoint = await context.Equipment.AddPointAsync(
            context.LimitedSession,
            context.ScopeA,
            new AddPointDefinition(
                equipmentId,
                PointId.From(Guid.Parse("64000000-0000-0000-0000-000000000002")),
                "RETURN-T",
                "Return temperature",
                Unit.FromSymbol("°C"),
                1));
        var crossScopeLocation = await context.Equipment.CreateEquipmentAsync(
            context.LimitedSession,
            new CreateEquipment(
                EquipmentId.From(Guid.Parse("63000000-0000-0000-0000-000000000002")),
                context.ScopeA,
                context.RootB,
                "BAD-SCOPE",
                "Invalid equipment"));
        var moved = await context.Equipment.MoveEquipmentAsync(
            context.LimitedSession,
            context.ScopeA,
            new MoveEquipment(equipmentId, context.AreaA, point.Value.Version));

        Assert.Equal(1, created.Value.Version);
        Assert.Equal(2, point.Value.Version);
        Assert.Equal("equipment.version_conflict", stalePoint.Error?.Code.Value);
        Assert.Equal("equipment.location_scope", crossScopeLocation.Error?.Code.Value);
        Assert.Equal(3, moved.Value.Version);

        var restarted = context.CreateEquipmentService();
        var registry = await restarted.ReadRegistryAsync(context.LimitedSession, context.ScopeA);
        Assert.True(registry.IsSuccess);
        var restoredEquipment = Assert.Single(registry.Value.Equipment);
        var restoredPoint = Assert.Single(registry.Value.Points);
        Assert.Equal(equipmentId, restoredEquipment.EquipmentId);
        Assert.Equal(context.AreaA, restoredEquipment.LocationId);
        Assert.Equal(pointId, restoredPoint.PointId);
        Assert.Equal(equipmentId, restoredPoint.EquipmentId);
        Assert.Equal("°C", restoredPoint.Unit.Symbol);
        await using var audit = context.DataSource.CreateCommand(
            $"SELECT count(*) FROM {EquipmentMigrations.Schema}.mutation_audit;");
        Assert.Equal(3L, await audit.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ScopedPermissionsAndOwnerRolesFailClosedWhileMutationsAreAudited()
    {
        await using var context = await FacilityEquipmentContext.CreateAsync(cluster);
        var deniedLocation = await context.Facilities.CreateLocationAsync(
            context.LimitedSession,
            new CreateLocation(
                LocationId.From(Guid.Parse("62000000-0000-0000-0000-000000000099")),
                context.ScopeB,
                context.RootB,
                "DENIED",
                "Denied"));
        var deniedEquipment = await context.Equipment.CreateEquipmentAsync(
            context.LimitedSession,
            new CreateEquipment(
                EquipmentId.From(Guid.Parse("63000000-0000-0000-0000-000000000099")),
                context.ScopeB,
                context.RootB,
                "DENIED",
                "Denied"));

        Assert.Equal("permission.denied", deniedLocation.Error?.Code.Value);
        Assert.Equal("permission.denied", deniedEquipment.Error?.Code.Value);

        await using var facilityAudit = context.DataSource.CreateCommand(
            $"SELECT count(*) FROM {FacilityMigrations.Schema}.mutation_audit;");
        await using var equipmentAudit = context.DataSource.CreateCommand(
            $"SELECT count(*) FROM {EquipmentMigrations.Schema}.mutation_audit;");
        Assert.Equal(4L, await facilityAudit.ExecuteScalarAsync());
        Assert.Equal(0L, await equipmentAudit.ExecuteScalarAsync());

        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand(
                         $"SET LOCAL ROLE \"{PostgreSqlClusterFixture.OwnerBRole}\";",
                         connection,
                         transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var foreignWrite = new NpgsqlCommand(
            $"""
            INSERT INTO {FacilityMigrations.Schema}.location
                (location_id, scope_id, physical_parent_id, code, name, version)
            VALUES
                ('62000000-0000-0000-0000-000000000098', '{context.ScopeA.Value}', NULL, 'FOREIGN', 'Foreign', 1);
            """,
            connection,
            transaction);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => foreignWrite.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private sealed class FacilityEquipmentContext : IAsyncDisposable
    {
        private FacilityEquipmentContext(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            FacilityScopeId scopeA,
            FacilityScopeId scopeB,
            LocationId rootA,
            LocationId areaA,
            LocationId roomA,
            LocationId rootB,
            SessionSnapshot allScopesSession,
            SessionSnapshot limitedSession)
        {
            Database = database;
            DataSource = dataSource;
            ScopeA = scopeA;
            ScopeB = scopeB;
            RootA = rootA;
            AreaA = areaA;
            RoomA = roomA;
            RootB = rootB;
            AllScopesSession = allScopesSession;
            LimitedSession = limitedSession;
            Facilities = CreateFacilityService();
            Equipment = CreateEquipmentService();
        }

        public TestDatabase Database { get; }
        public NpgsqlDataSource DataSource { get; }
        public FacilityScopeId ScopeA { get; }
        public FacilityScopeId ScopeB { get; }
        public LocationId RootA { get; }
        public LocationId AreaA { get; }
        public LocationId RoomA { get; }
        public LocationId RootB { get; }
        public SessionSnapshot AllScopesSession { get; }
        public SessionSnapshot LimitedSession { get; }
        public FacilityService Facilities { get; }
        public EquipmentService Equipment { get; }

        public static async Task<FacilityEquipmentContext> CreateAsync(PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                FacilityMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                EquipmentMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));

            var scopeA = FacilityScopeId.From(Guid.Parse("61000000-0000-0000-0000-000000000001"));
            var scopeB = FacilityScopeId.From(Guid.Parse("61000000-0000-0000-0000-000000000002"));
            var rootA = LocationId.From(Guid.Parse("62000000-0000-0000-0000-000000000001"));
            var areaA = LocationId.From(Guid.Parse("62000000-0000-0000-0000-000000000002"));
            var roomA = LocationId.From(Guid.Parse("62000000-0000-0000-0000-000000000003"));
            var rootB = LocationId.From(Guid.Parse("62000000-0000-0000-0000-000000000004"));
            var allScopesSession = CreateSession([
                FacilityPermissions.Read(scopeA),
                FacilityPermissions.Write(scopeA),
                FacilityPermissions.Read(scopeB),
                FacilityPermissions.Write(scopeB),
                EquipmentPermissions.Read(scopeA),
                EquipmentPermissions.Write(scopeA),
                EquipmentPermissions.Read(scopeB),
                EquipmentPermissions.Write(scopeB),
            ]);
            var limitedSession = CreateSession([
                FacilityPermissions.Read(scopeA),
                FacilityPermissions.Write(scopeA),
                EquipmentPermissions.Read(scopeA),
                EquipmentPermissions.Write(scopeA),
            ]);
            var context = new FacilityEquipmentContext(
                database,
                dataSource,
                scopeA,
                scopeB,
                rootA,
                areaA,
                roomA,
                rootB,
                allScopesSession,
                limitedSession);
            Assert.True((await context.Facilities.CreateLocationAsync(
                allScopesSession,
                new CreateLocation(rootA, scopeA, null, "SITE-A", "Site A"))).IsSuccess);
            Assert.True((await context.Facilities.CreateLocationAsync(
                allScopesSession,
                new CreateLocation(areaA, scopeA, rootA, "AREA-A", "Area A"))).IsSuccess);
            Assert.True((await context.Facilities.CreateLocationAsync(
                allScopesSession,
                new CreateLocation(roomA, scopeA, areaA, "ROOM-A", "Room A"))).IsSuccess);
            Assert.True((await context.Facilities.CreateLocationAsync(
                allScopesSession,
                new CreateLocation(rootB, scopeB, null, "SITE-B", "Site B"))).IsSuccess);
            return context;
        }

        public FacilityService CreateFacilityService() => new(
            new FacilityStore(DataSource, PostgreSqlClusterFixture.OwnerARole, SystemClock.Instance),
            SystemClock.Instance);

        public EquipmentService CreateEquipmentService() => new(
            new EquipmentStore(DataSource, PostgreSqlClusterFixture.OwnerBRole, SystemClock.Instance),
            new FacilityStore(DataSource, PostgreSqlClusterFixture.OwnerARole, SystemClock.Instance),
            SystemClock.Instance);

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }

        private static SessionSnapshot CreateSession(IReadOnlyCollection<PermissionCode> permissions)
        {
            var now = DateTimeOffset.UtcNow;
            return new SessionSnapshot(
                SessionId.New(),
                SubjectId.New(),
                PrincipalKind.User,
                now.AddMinutes(-1),
                now.AddHours(1),
                new EffectivePermissions(permissions));
        }
    }
}
