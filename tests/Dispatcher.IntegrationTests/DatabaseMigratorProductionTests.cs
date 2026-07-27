using Dispatcher.Core;
using Dispatcher.DatabaseMigrator;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class DatabaseMigratorProductionTests
{
    private const string ChecksumFailureMessage =
        "A stored migration checksum does not match the current SQL.";
    private const string OwnershipFailureMessage =
        "The schema owner does not match the configured PostgreSQL role.";

    private readonly PostgreSqlClusterFixture cluster;

    public DatabaseMigratorProductionTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task FreshDatabaseAppliesAllProductionMigrationsAndRepeatIsSafe()
    {
        await using TestDatabase database = await cluster.CreateDatabaseAsync();
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        MigrationConfiguration configuration = CreateConfiguration(database.ConnectionString);

        DatabaseMigrationPreflightResult preflight = await DatabaseMigrationPreflight.RunAsync(
            dataSource,
            configuration,
            CancellationToken.None);
        Assert.True(preflight.IsValid, string.Join(Environment.NewLine, preflight.Errors));
        Assert.Equal(MigrationCatalog.ExpectedPlanCount, preflight.CheckedRoleCount);

        int expectedStepCount = MigrationCatalog.Registrations.Sum(static registration =>
            registration.CreatePlan(registration.Owner).Steps.Count);
        DatabaseMigrationResult firstRun = await DatabaseMigrationCoordinator.ApplyAsync(
            dataSource,
            configuration,
            CancellationToken.None);

        Assert.True(firstRun.IsSuccess, firstRun.FailureReason);
        Assert.Equal(MigrationCatalog.ExpectedPlanCount, firstRun.CompletedOwners.Count);
        Assert.Equal(expectedStepCount, firstRun.AppliedStepCount);
        Assert.Equal(
            (long)MigrationCatalog.ExpectedPlanCount,
            await CountProductionSchemasAsync(dataSource));
        Assert.Equal(
            (long)MigrationCatalog.ExpectedPlanCount,
            await CountMigrationHistoryTablesAsync(dataSource));

        DatabaseMigrationResult repeatRun = await DatabaseMigrationCoordinator.ApplyAsync(
            dataSource,
            configuration,
            CancellationToken.None);

        Assert.True(repeatRun.IsSuccess, repeatRun.FailureReason);
        Assert.Equal(MigrationCatalog.ExpectedPlanCount, repeatRun.CompletedOwners.Count);
        Assert.Equal(0, repeatRun.AppliedStepCount);
    }

    [Fact]
    public async Task MissingOwnerMappingIsRejectedBeforeDatabaseChanges()
    {
        await using TestDatabase database = await cluster.CreateDatabaseAsync();
        Dictionary<string, string?> environmentVariables = CreateEnvironment(database.ConnectionString);
        MigrationPlanRegistration missingRegistration = MigrationCatalog.Registrations[^1];
        string missingVariable = MigrationEnvironmentVariables.GetRoleVariableName(missingRegistration.Owner);
        Assert.True(environmentVariables.Remove(missingVariable));

        MigrationConfigurationValidationResult validation = MigrationConfigurationParser.Parse(
            environmentVariables,
            MigrationCatalog.Registrations);

        Assert.False(validation.IsValid);
        Assert.Contains(
            $"Required environment variable '{missingVariable}' is not configured.",
            validation.Errors);
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        Assert.Equal(0L, await CountProductionSchemasAsync(dataSource));
        Assert.Equal(0L, await CountMigrationHistoryTablesAsync(dataSource));
    }

    [Fact]
    public async Task StoredChecksumConflictFailsBeforeAnyOwnerCompletes()
    {
        await using TestDatabase database = await cluster.CreateDatabaseAsync();
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        MigrationConfiguration configuration = CreateConfiguration(database.ConnectionString);
        DatabaseMigrationResult firstRun = await DatabaseMigrationCoordinator.ApplyAsync(
            dataSource,
            configuration,
            CancellationToken.None);
        Assert.True(firstRun.IsSuccess, firstRun.FailureReason);

        await using (NpgsqlCommand corruptChecksum = dataSource.CreateCommand(
            "UPDATE administration.__dispatcher_migrations " +
            "SET checksum = repeat('0', 64) WHERE version = 1;"))
        {
            Assert.Equal(1, await corruptChecksum.ExecuteNonQueryAsync());
        }

        DatabaseMigrationResult repeatRun = await DatabaseMigrationCoordinator.ApplyAsync(
            dataSource,
            configuration,
            CancellationToken.None);

        Assert.False(repeatRun.IsSuccess);
        Assert.Empty(repeatRun.CompletedOwners);
        Assert.Equal("administration", repeatRun.FailedOwner);
        Assert.Equal("administration", repeatRun.FailedSchema);
        Assert.Equal(ChecksumFailureMessage, repeatRun.FailureReason);
    }

    [Fact]
    public async Task OwnerFailureStopsBeforeFollowingPlans()
    {
        await using TestDatabase database = await cluster.CreateDatabaseAsync();
        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        MigrationConfiguration configuration = CreateConfiguration(database.ConnectionString);
        await using (NpgsqlCommand createConflictingSchema = dataSource.CreateCommand(
            "CREATE SCHEMA dashboards AUTHORIZATION postgres;"))
        {
            await createConflictingSchema.ExecuteNonQueryAsync();
        }

        DatabaseMigrationResult result = await DatabaseMigrationCoordinator.ApplyAsync(
            dataSource,
            configuration,
            CancellationToken.None);

        string[] expectedCompletedOwners = MigrationCatalog.Registrations
            .TakeWhile(static registration =>
                !string.Equals(registration.Owner, "dashboards", StringComparison.Ordinal))
            .Select(static registration => registration.Owner)
            .ToArray();
        Assert.False(result.IsSuccess);
        Assert.Equal(
            expectedCompletedOwners,
            result.CompletedOwners.Select(static owner => owner.Owner).ToArray());
        Assert.Equal("dashboards", result.FailedOwner);
        Assert.Equal("dashboards", result.FailedSchema);
        Assert.Equal(OwnershipFailureMessage, result.FailureReason);
        Assert.Equal(0L, await CountSchemaAsync(dataSource, "equipment_registry"));
    }

    private static MigrationConfiguration CreateConfiguration(string connectionString) =>
        new(connectionString, CreateRoleMappings());

    private static Dictionary<string, string?> CreateEnvironment(string connectionString)
    {
        var environmentVariables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [MigrationEnvironmentVariables.ConnectionString] = connectionString,
        };
        foreach (MigrationPlanRegistration registration in MigrationCatalog.Registrations)
        {
            environmentVariables.Add(
                MigrationEnvironmentVariables.GetRoleVariableName(registration.Owner),
                registration.Owner);
        }

        environmentVariables.Add(
            MigrationEnvironmentVariables.GetRoleVariableName(
                CoreRuntimeMigrations.PublishedReadRoleKey),
            CoreRuntimeMigrations.Owner);
        return environmentVariables;
    }

    private static Dictionary<string, string> CreateRoleMappings()
    {
        Dictionary<string, string> mappings = MigrationCatalog.Registrations.ToDictionary(
            static registration => registration.Owner,
            static registration => registration.Owner,
            StringComparer.Ordinal);
        mappings.Add(
            CoreRuntimeMigrations.PublishedReadRoleKey,
            CoreRuntimeMigrations.Owner);
        return mappings;
    }

    private static async Task<long> CountProductionSchemasAsync(NpgsqlDataSource dataSource)
    {
        string[] schemas = MigrationCatalog.Registrations
            .Select(static registration => registration.Schema)
            .ToArray();
        await using NpgsqlCommand command = dataSource.CreateCommand(
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name = ANY(@schemas);");
        command.Parameters.AddWithValue("schemas", schemas);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountMigrationHistoryTablesAsync(NpgsqlDataSource dataSource)
    {
        string[] schemas = MigrationCatalog.Registrations
            .Select(static registration => registration.Schema)
            .ToArray();
        await using NpgsqlCommand command = dataSource.CreateCommand(
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_name = '__dispatcher_migrations' AND table_schema = ANY(@schemas);");
        command.Parameters.AddWithValue("schemas", schemas);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountSchemaAsync(NpgsqlDataSource dataSource, string schema)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name = @schema;");
        command.Parameters.AddWithValue("schema", schema);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
