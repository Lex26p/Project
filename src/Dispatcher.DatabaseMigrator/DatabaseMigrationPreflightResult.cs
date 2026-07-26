using System.Collections.ObjectModel;

namespace Dispatcher.DatabaseMigrator;

public sealed class DatabaseMigrationPreflightResult
{
    internal DatabaseMigrationPreflightResult(
        int serverMajorVersion,
        int checkedRoleCount,
        List<string> errors)
    {
        if (serverMajorVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(serverMajorVersion),
                "The PostgreSQL server major version must be positive.");
        }

        if (checkedRoleCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(checkedRoleCount),
                "The checked PostgreSQL role count cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(errors);

        ServerMajorVersion = serverMajorVersion;
        CheckedRoleCount = checkedRoleCount;
        Errors = Array.AsReadOnly(errors.ToArray());
    }

    public int ServerMajorVersion { get; }

    public int CheckedRoleCount { get; }

    public ReadOnlyCollection<string> Errors { get; }

    public bool IsValid => Errors.Count == 0;
}
