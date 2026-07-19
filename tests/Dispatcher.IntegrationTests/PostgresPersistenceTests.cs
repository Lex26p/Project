using Dispatcher.Persistence;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class PostgresPersistenceTests
{
    private const string OwnerASchema = "owner_a";
    private const string OwnerBSchema = "owner_b";
    private readonly PostgreSqlClusterFixture cluster;

    public PostgresPersistenceTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task FreshDatabaseAppliesAllMigrationsAndRepeatIsSafe()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var plan = CreateOwnerPlan(OwnerASchema, PostgreSqlClusterFixture.OwnerARole);

        Assert.Equal(2, await PostgresMigrationRunner.ApplyAsync(dataSource, plan));
        Assert.Equal(0, await PostgresMigrationRunner.ApplyAsync(dataSource, plan));

        await using var command = dataSource.CreateCommand(
            $"SELECT count(*) FROM {OwnerASchema}.__dispatcher_migrations;");
        Assert.Equal(2L, await command.ExecuteScalarAsync());

        var changedPlan = new ModuleMigrationPlan(
            "owner_a",
            OwnerASchema,
            PostgreSqlClusterFixture.OwnerARole,
            [new MigrationStep(1, "changed", "SELECT 1;")]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgresMigrationRunner.ApplyAsync(dataSource, changedPlan));
    }

    [Fact]
    public async Task FailedMigrationRollsBackThePendingOwnerPlan()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var plan = new ModuleMigrationPlan(
            "owner_a",
            OwnerASchema,
            PostgreSqlClusterFixture.OwnerARole,
            [
                new MigrationStep(1, "valid", $"CREATE TABLE {OwnerASchema}.valid_table (id integer PRIMARY KEY);"),
                new MigrationStep(2, "invalid", $"CREATE TABLE {OwnerASchema}.invalid_table ("),
            ]);

        await Assert.ThrowsAsync<PostgresException>(() => PostgresMigrationRunner.ApplyAsync(dataSource, plan));

        await using var command = dataSource.CreateCommand(
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name = @schema;");
        command.Parameters.AddWithValue("schema", OwnerASchema);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task OwnerRoleCannotWriteForeignSchema()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            CreateOwnerPlan(OwnerASchema, PostgreSqlClusterFixture.OwnerARole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            CreateOwnerPlan(OwnerBSchema, PostgreSqlClusterFixture.OwnerBRole));

        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetRoleAsync(connection, transaction, PostgreSqlClusterFixture.OwnerARole);
            await using var ownWrite = new NpgsqlCommand(
                $"INSERT INTO {OwnerASchema}.aggregate_state (id, version) VALUES (2, 0);",
                connection,
                transaction);
            await ownWrite.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetRoleAsync(connection, transaction, PostgreSqlClusterFixture.OwnerARole);
            await using var foreignWrite = new NpgsqlCommand(
                $"INSERT INTO {OwnerBSchema}.aggregate_state (id, version) VALUES (2, 0);",
                connection,
                transaction);
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => foreignWrite.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }
    }

    [Fact]
    public async Task OwnerTransitionAndMandatoryObligationAreAtomic()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            CreateOwnerPlan(OwnerASchema, PostgreSqlClusterFixture.OwnerARole));

        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetRoleAsync(connection, transaction, PostgreSqlClusterFixture.OwnerARole);
            await ExecuteAsync(
                connection,
                transaction,
                $"UPDATE {OwnerASchema}.aggregate_state SET version = 1 WHERE id = 1;");
            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO {OwnerASchema}.mandatory_obligation (id, state_id, kind) VALUES (gen_random_uuid(), 1, 'audit');");
            await transaction.CommitAsync();
        }

        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await SetRoleAsync(connection, transaction, PostgreSqlClusterFixture.OwnerARole);
            await ExecuteAsync(
                connection,
                transaction,
                $"UPDATE {OwnerASchema}.aggregate_state SET version = 2 WHERE id = 1;");
            var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO {OwnerASchema}.mandatory_obligation (id, state_id, kind) VALUES (gen_random_uuid(), 1, NULL);"));
            Assert.Equal(PostgresErrorCodes.NotNullViolation, exception.SqlState);
        }

        await using var stateCommand = dataSource.CreateCommand(
            $"SELECT version FROM {OwnerASchema}.aggregate_state WHERE id = 1;");
        Assert.Equal(1L, await stateCommand.ExecuteScalarAsync());

        await using var obligationCommand = dataSource.CreateCommand(
            $"SELECT count(*) FROM {OwnerASchema}.mandatory_obligation;");
        Assert.Equal(1L, await obligationCommand.ExecuteScalarAsync());
    }

    [Fact]
    public async Task OwnerStateAndMigrationHistorySurviveDumpAndRestore()
    {
        await using var sourceDatabase = await cluster.CreateDatabaseAsync();
        await using (var sourceDataSource = NpgsqlDataSource.Create(sourceDatabase.ConnectionString))
        {
            await PostgresMigrationRunner.ApplyAsync(
                sourceDataSource,
                CreateOwnerPlan(OwnerASchema, PostgreSqlClusterFixture.OwnerARole));
            await using var update = sourceDataSource.CreateCommand(
                $"UPDATE {OwnerASchema}.aggregate_state SET version = 7 WHERE id = 1;");
            await update.ExecuteNonQueryAsync();
        }

        var dumpPath = await cluster.DumpDatabaseAsync(sourceDatabase);
        await using var restoredDatabase = await cluster.CreateDatabaseAsync();
        await cluster.RestoreDatabaseAsync(restoredDatabase, dumpPath);
        await using var restoredDataSource = NpgsqlDataSource.Create(restoredDatabase.ConnectionString);

        await using var stateCommand = restoredDataSource.CreateCommand(
            $"SELECT version FROM {OwnerASchema}.aggregate_state WHERE id = 1;");
        Assert.Equal(7L, await stateCommand.ExecuteScalarAsync());

        await using var historyCommand = restoredDataSource.CreateCommand(
            $"SELECT count(*) FROM {OwnerASchema}.__dispatcher_migrations;");
        Assert.Equal(2L, await historyCommand.ExecuteScalarAsync());
    }

    private static ModuleMigrationPlan CreateOwnerPlan(string schema, string role) => new(
        schema,
        schema,
        role,
        [
            new MigrationStep(
                1,
                "owner state and mandatory obligation",
                $"""
                CREATE TABLE {schema}.aggregate_state (
                    id integer PRIMARY KEY,
                    version bigint NOT NULL CHECK (version >= 0)
                );
                CREATE TABLE {schema}.mandatory_obligation (
                    id uuid PRIMARY KEY,
                    state_id integer NOT NULL REFERENCES {schema}.aggregate_state(id),
                    kind text NOT NULL
                );
                INSERT INTO {schema}.aggregate_state (id, version) VALUES (1, 0);
                """),
            new MigrationStep(
                2,
                "obligation lookup",
                $"CREATE INDEX mandatory_obligation_state_idx ON {schema}.mandatory_obligation (state_id);"),
        ]);

    private static async Task SetRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string role)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE \"{role}\";", connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }
}
