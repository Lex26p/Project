using System.Collections.ObjectModel;

namespace Dispatcher.DatabaseMigrator;

public sealed class MigrationConfiguration
{
    public MigrationConfiguration(
        string connectionString,
        Dictionary<string, string> databaseRolesByOwner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(databaseRolesByOwner);

        ConnectionString = connectionString;
        DatabaseRolesByOwner = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(databaseRolesByOwner, StringComparer.Ordinal));
    }

    public string ConnectionString { get; }

    public ReadOnlyDictionary<string, string> DatabaseRolesByOwner { get; }

    public string GetDatabaseRole(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return DatabaseRolesByOwner.TryGetValue(owner, out string? role)
            ? role
            : throw new InvalidOperationException($"No PostgreSQL role is configured for migration owner '{owner}'.");
    }
}
