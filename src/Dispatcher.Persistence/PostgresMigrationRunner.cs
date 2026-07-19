using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Dispatcher.Persistence;

public static class PostgresMigrationRunner
{
    public static async Task<int> ApplyAsync(
        NpgsqlDataSource dataSource,
        ModuleMigrationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(plan);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await AcquireOwnerLockAsync(connection, plan.Owner, cancellationToken).ConfigureAwait(false);
        await EnsureOwnerSchemaAsync(connection, plan, cancellationToken).ConfigureAwait(false);
        await SetOwnerRoleAsync(connection, plan.DatabaseRole, cancellationToken).ConfigureAwait(false);
        await EnsureHistoryTableAsync(connection, plan.Schema, cancellationToken).ConfigureAwait(false);

        var applied = await ReadAppliedMigrationsAsync(connection, plan.Schema, cancellationToken).ConfigureAwait(false);
        var appliedCount = 0;

        foreach (var step in plan.Steps)
        {
            var checksum = ComputeChecksum(step.Sql);
            if (applied.TryGetValue(step.Version, out var existingChecksum))
            {
                if (!string.Equals(existingChecksum, checksum, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Migration {step.Version} for owner '{plan.Owner}' has a different checksum.");
                }

                continue;
            }

            await using (var migrationCommand = new NpgsqlCommand(step.Sql, connection))
            {
                await migrationCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await RecordMigrationAsync(connection, plan.Schema, step, checksum, cancellationToken).ConfigureAwait(false);
            appliedCount++;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return appliedCount;
    }

    private static async Task AcquireOwnerLockAsync(
        NpgsqlConnection connection,
        string owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@owner, 0));",
            connection);
        command.Parameters.AddWithValue("owner", owner);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureOwnerSchemaAsync(
        NpgsqlConnection connection,
        ModuleMigrationPlan plan,
        CancellationToken cancellationToken)
    {
        var schema = ModuleMigrationPlan.QuoteIdentifier(plan.Schema);
        var role = ModuleMigrationPlan.QuoteIdentifier(plan.DatabaseRole);

        await using (var createCommand = new NpgsqlCommand(
                         $"CREATE SCHEMA IF NOT EXISTS {schema} AUTHORIZATION {role};",
                         connection))
        {
            await createCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var ownerCommand = new NpgsqlCommand(
            "SELECT schema_owner FROM information_schema.schemata WHERE schema_name = @schema;",
            connection);
        ownerCommand.Parameters.AddWithValue("schema", plan.Schema);
        var actualOwner = (string?)await ownerCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (!string.Equals(actualOwner, plan.DatabaseRole, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Schema '{plan.Schema}' is not owned by expected role '{plan.DatabaseRole}'.");
        }
    }

    private static async Task SetOwnerRoleAsync(
        NpgsqlConnection connection,
        string databaseRole,
        CancellationToken cancellationToken)
    {
        var role = ModuleMigrationPlan.QuoteIdentifier(databaseRole);
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE {role};", connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureHistoryTableAsync(
        NpgsqlConnection connection,
        string schemaName,
        CancellationToken cancellationToken)
    {
        var schema = ModuleMigrationPlan.QuoteIdentifier(schemaName);
        await using var command = new NpgsqlCommand(
            $"""
            CREATE TABLE IF NOT EXISTS {schema}.__dispatcher_migrations (
                version bigint PRIMARY KEY CHECK (version > 0),
                name text NOT NULL,
                checksum character(64) NOT NULL,
                applied_at timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """,
            connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Dictionary<long, string>> ReadAppliedMigrationsAsync(
        NpgsqlConnection connection,
        string schemaName,
        CancellationToken cancellationToken)
    {
        var schema = ModuleMigrationPlan.QuoteIdentifier(schemaName);
        await using var command = new NpgsqlCommand(
            $"SELECT version, checksum FROM {schema}.__dispatcher_migrations ORDER BY version;",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var applied = new Dictionary<long, string>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied.Add(reader.GetInt64(0), reader.GetString(1));
        }

        return applied;
    }

    private static async Task RecordMigrationAsync(
        NpgsqlConnection connection,
        string schemaName,
        MigrationStep step,
        string checksum,
        CancellationToken cancellationToken)
    {
        var schema = ModuleMigrationPlan.QuoteIdentifier(schemaName);
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {schema}.__dispatcher_migrations (version, name, checksum) VALUES (@version, @name, @checksum);",
            connection);
        command.Parameters.AddWithValue("version", step.Version);
        command.Parameters.AddWithValue("name", step.Name);
        command.Parameters.AddWithValue("checksum", checksum);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeChecksum(string sql) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
}
