using System.Text.Json;
using Dispatcher.Configuration;
using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Server;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class EquipmentStagingTests
{
    private readonly PostgreSqlClusterFixture cluster;

    public EquipmentStagingTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task CreatedRequiresEquipmentAndConfigurationAcceptanceAndReconcilesIdempotently()
    {
        await using var context = await StagingContext.CreateAsync(cluster);
        var row = context.SnmpRow("private-community");

        var partial = Assert.Single(await context.Service.ProcessAsync(context.EquipmentOnlySession, [row]));
        Assert.False(partial.Created);
        Assert.Equal(StagingRowState.EquipmentAccepted, partial.State);
        Assert.Equal("initial_configuration", Assert.Single(partial.Errors).Field);
        Assert.Equal(1L, await context.CountAsync(EquipmentMigrations.Schema, "equipment"));
        Assert.Equal(0L, await context.CountAsync(
            ConfigurationMigrations.Schema,
            "initial_configuration_obligation"));

        var recovered = await context.Service.ReconcileAsync(context.FullSession, context.ScopeId, row.RowId);
        Assert.True(recovered.Created);
        var replay = Assert.Single(await context.Service.ProcessAsync(context.FullSession, [row]));
        Assert.True(replay.Created);
        Assert.Equal(1L, await context.CountAsync(EquipmentMigrations.Schema, "equipment"));
        Assert.Equal(1L, await context.CountAsync(EquipmentMigrations.Schema, "staging_row"));
        Assert.Equal(1L, await context.CountAsync(
            ConfigurationMigrations.Schema,
            "initial_configuration_obligation"));

        var obligation = await context.Configuration.ReadAsync(context.FullSession, context.ScopeId, row.RowId);
        Assert.True(obligation.Value.HasSecret);
        Assert.DoesNotContain("private-community", JsonSerializer.Serialize(obligation.Value), StringComparison.Ordinal);
        var staged = await context.Staging.ReadWorkItemAsync(context.ScopeId, row.RowId);
        Assert.True(staged.Value.Snapshot.HasSecret);
        Assert.DoesNotContain("private-community", JsonSerializer.Serialize(staged.Value.Snapshot), StringComparison.Ordinal);
        Assert.DoesNotContain("private-community", await context.ReadStoredTextAsync(), StringComparison.Ordinal);

        var conflict = Assert.Single(await context.Service.ProcessAsync(
            context.FullSession,
            [row with { Code = "DIFFERENT" }]));
        Assert.False(conflict.Created);
        Assert.Equal("staging.idempotency_conflict", Assert.Single(conflict.Errors).Code);
    }

    [Fact]
    public async Task MultiRowResultsKeepInvalidRowsOutAndModbusCopyDoesNotChangeHost()
    {
        await using var context = await StagingContext.CreateAsync(cluster);
        var source = new StagingRowInput(
            Guid.CreateVersion7(),
            EquipmentId.From(Guid.CreateVersion7()),
            context.ScopeId,
            context.LocationId,
            "PLC",
            "PLC",
            EquipmentProtocolForm.NewModbusTcp() with { Host = "plc.example", ModbusUnitId = 20 });
        var valid = EquipmentStagingTools.Copy(source, 2, incrementModbusUnitId: true);
        var invalid = source with
        {
            RowId = Guid.CreateVersion7(),
            EquipmentId = EquipmentId.From(Guid.CreateVersion7()),
            Code = string.Empty,
        };

        var results = await context.Service.ProcessAsync(context.FullSession, [.. valid, invalid]);
        Assert.Equal(3, results.Count);
        Assert.Equal(2, results.Count(result => result.Created));
        Assert.Equal("code", Assert.Single(results.Single(result => !result.Created).Errors).Field);
        Assert.Equal(2L, await context.CountAsync(EquipmentMigrations.Schema, "equipment"));
        Assert.All(valid, row => Assert.Equal("plc.example", row.Form.Host));
        Assert.Equal([21, 22], valid.Select(row => row.Form.ModbusUnitId));
    }

    private sealed class StagingContext : IAsyncDisposable
    {
        private StagingContext(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            FacilityScopeId scopeId,
            LocationId locationId,
            SessionSnapshot equipmentOnlySession,
            SessionSnapshot fullSession,
            EquipmentStagingStore staging,
            InitialConfigurationService configuration,
            EquipmentStagingService service)
        {
            Database = database;
            DataSource = dataSource;
            ScopeId = scopeId;
            LocationId = locationId;
            EquipmentOnlySession = equipmentOnlySession;
            FullSession = fullSession;
            Staging = staging;
            Configuration = configuration;
            Service = service;
        }

        public TestDatabase Database { get; }
        public NpgsqlDataSource DataSource { get; }
        public FacilityScopeId ScopeId { get; }
        public LocationId LocationId { get; }
        public SessionSnapshot EquipmentOnlySession { get; }
        public SessionSnapshot FullSession { get; }
        public EquipmentStagingStore Staging { get; }
        public InitialConfigurationService Configuration { get; }
        public EquipmentStagingService Service { get; }

        public static async Task<StagingContext> CreateAsync(PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                FacilityMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                EquipmentMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                ConfigurationMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
            var scopeId = FacilityScopeId.From(Guid.Parse("91000000-0000-0000-0000-000000000001"));
            var locationId = LocationId.From(Guid.Parse("92000000-0000-0000-0000-000000000001"));
            var now = DateTimeOffset.UtcNow;
            var allPermissions = new[]
            {
                FacilityPermissions.Read(scopeId),
                FacilityPermissions.Write(scopeId),
                EquipmentPermissions.Read(scopeId),
                EquipmentPermissions.Write(scopeId),
                ConfigurationPermissions.Read(scopeId),
                ConfigurationPermissions.Save(scopeId),
            };
            var fullSession = Session(now, allPermissions);
            var equipmentOnlySession = Session(now, allPermissions.Where(
                permission => permission != ConfigurationPermissions.Save(scopeId)).ToArray());
            var facilityStore = new FacilityStore(dataSource, PostgreSqlClusterFixture.OwnerARole, SystemClock.Instance);
            var facilities = new FacilityService(facilityStore, SystemClock.Instance);
            Assert.True((await facilities.CreateLocationAsync(
                fullSession,
                new CreateLocation(locationId, scopeId, null, "STAGE", "Staging site"))).IsSuccess);
            var equipment = new EquipmentService(
                new EquipmentStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, SystemClock.Instance),
                facilityStore,
                SystemClock.Instance);
            var staging = new EquipmentStagingStore(
                dataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                SystemClock.Instance);
            var configuration = new InitialConfigurationService(
                new InitialConfigurationStore(dataSource, PostgreSqlClusterFixture.OwnerARole, SystemClock.Instance),
                SystemClock.Instance);
            var service = new EquipmentStagingService(
                staging,
                equipment,
                configuration,
                new StagingSecretProtector(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
                SystemClock.Instance);
            return new StagingContext(
                database,
                dataSource,
                scopeId,
                locationId,
                equipmentOnlySession,
                fullSession,
                staging,
                configuration,
                service);
        }

        public StagingRowInput SnmpRow(string secret) => new(
            Guid.Parse("93000000-0000-0000-0000-000000000001"),
            EquipmentId.From(Guid.Parse("94000000-0000-0000-0000-000000000001")),
            ScopeId,
            LocationId,
            "SWITCH-1",
            "Switch",
            EquipmentProtocolForm.NewSnmp() with
            {
                Host = "switch.example",
                Secret = WriteOnlySecret.From(secret),
            });

        public async Task<long> CountAsync(string schema, string table)
        {
            await using var command = DataSource.CreateCommand($"SELECT count(*) FROM {schema}.{table};");
            return (long)(await command.ExecuteScalarAsync() ?? 0L);
        }

        public async Task<string> ReadStoredTextAsync()
        {
            await using var command = DataSource.CreateCommand(
                $"""
                SELECT coalesce(string_agg(value, ' '), '') FROM (
                    SELECT encode(protected_secret, 'hex') AS value
                    FROM {EquipmentMigrations.Schema}.staging_row
                    UNION ALL
                    SELECT encode(protected_secret, 'hex') AS value
                    FROM {ConfigurationMigrations.Schema}.initial_configuration_obligation
                    UNION ALL
                    SELECT action || ' ' || permission AS value
                    FROM {EquipmentMigrations.Schema}.staging_audit
                    UNION ALL
                    SELECT action || ' ' || permission AS value
                    FROM {ConfigurationMigrations.Schema}.obligation_audit
                ) values;
                """);
            return (string)(await command.ExecuteScalarAsync() ?? string.Empty);
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }

        private static SessionSnapshot Session(DateTimeOffset now, IReadOnlyCollection<PermissionCode> permissions) =>
            new(
                SessionId.New(),
                SubjectId.New(),
                PrincipalKind.User,
                now.AddMinutes(-1),
                now.AddHours(1),
                new EffectivePermissions(permissions));
    }
}
