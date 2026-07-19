using System.Text.RegularExpressions;
using Npgsql;

namespace Dispatcher.Platform;

internal sealed partial class PlatformDatabaseRole
{
    private PlatformDatabaseRole(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public string Quoted => $"\"{Value}\"";

    public static PlatformDatabaseRole From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return RolePattern().IsMatch(value)
            ? new PlatformDatabaseRole(value)
            : throw new ArgumentException("Invalid PostgreSQL role name.", nameof(value));
    }

    public async Task SetLocalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE {Quoted};", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
