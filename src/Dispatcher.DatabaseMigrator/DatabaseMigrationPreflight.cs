using Npgsql;

namespace Dispatcher.DatabaseMigrator;

public static class DatabaseMigrationPreflight
{
    private const int MinimumSupportedServerVersionNumber = 170000;

    public static async Task<DatabaseMigrationPreflightResult> RunAsync(
        NpgsqlDataSource dataSource,
        MigrationConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configuration);

        await using NpgsqlConnection connection =
            await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        int serverVersionNumber = await ReadServerVersionNumberAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        int serverMajorVersion = serverVersionNumber / 10000;
        var errors = new List<string>();

        if (serverVersionNumber < MinimumSupportedServerVersionNumber)
        {
            errors.Add(
                $"PostgreSQL 17 or later is required; connected server major version is {serverMajorVersion}.");
        }

        string[] configuredRoles = configuration.DatabaseRolesByOwner.Values
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        HashSet<string> existingRoles = await ReadExistingRolesAsync(
            connection,
            configuredRoles,
            cancellationToken).ConfigureAwait(false);

        foreach (string databaseRole in configuredRoles)
        {
            if (!existingRoles.Contains(databaseRole))
            {
                errors.Add($"Required PostgreSQL role '{databaseRole}' does not exist.");
                continue;
            }

            bool canSetRole = await CanSetRoleAsync(connection, databaseRole, cancellationToken)
                .ConfigureAwait(false);
            if (!canSetRole)
            {
                errors.Add(
                    $"Migration principal cannot SET ROLE to PostgreSQL role '{databaseRole}'.");
            }
        }

        return new DatabaseMigrationPreflightResult(
            serverMajorVersion,
            configuredRoles.Length,
            errors);
    }

    private static async Task<int> ReadServerVersionNumberAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT current_setting('server_version_num')::integer;",
            connection);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is int versionNumber
            ? versionNumber
            : throw new InvalidOperationException("PostgreSQL did not return a valid server version number.");
    }

    private static async Task<HashSet<string>> ReadExistingRolesAsync(
        NpgsqlConnection connection,
        string[] configuredRoles,
        CancellationToken cancellationToken)
    {
        var existingRoles = new HashSet<string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(
            "SELECT rolname FROM pg_catalog.pg_roles WHERE rolname = ANY(@roles) ORDER BY rolname;",
            connection);
        command.Parameters.AddWithValue("roles", configuredRoles);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            existingRoles.Add(reader.GetString(0));
        }

        return existingRoles;
    }

    private static async Task<bool> CanSetRoleAsync(
        NpgsqlConnection connection,
        string databaseRole,
        CancellationToken cancellationToken)
    {
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string quotedRole = $"\"{databaseRole}\"";
            await using var command = new NpgsqlCommand($"SET LOCAL ROLE {quotedRole};", connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (PostgresException exception) when (
            string.Equals(exception.SqlState, PostgresErrorCodes.InsufficientPrivilege, StringComparison.Ordinal) ||
            string.Equals(exception.SqlState, PostgresErrorCodes.InvalidRoleSpecification, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }
}
