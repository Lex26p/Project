namespace Dispatcher.DatabaseMigrator;

public static class MigrationEnvironmentVariables
{
    public const string ConnectionString = "DISPATCHER_MIGRATIONS_CONNECTION_STRING";

    public const string RolePrefix = "DISPATCHER_MIGRATIONS_ROLE__";

    public static string GetRoleVariableName(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return $"{RolePrefix}{owner}";
    }
}
