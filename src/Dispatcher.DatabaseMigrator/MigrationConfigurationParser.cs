using System.Collections.ObjectModel;
using Dispatcher.Core;

namespace Dispatcher.DatabaseMigrator;

public static class MigrationConfigurationParser
{
    public static MigrationConfigurationValidationResult Parse(
        Dictionary<string, string?> environmentVariables,
        ReadOnlyCollection<MigrationPlanRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);
        ArgumentNullException.ThrowIfNull(registrations);

        var errors = new List<string>();
        Dictionary<string, string?> normalizedVariables = Normalize(environmentVariables, errors);
        string? connectionString = ReadRequiredValue(
            normalizedVariables,
            MigrationEnvironmentVariables.ConnectionString,
            errors);

        var rolesByOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        var knownOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (MigrationPlanRegistration registration in registrations)
        {
            knownOwners.Add(registration.Owner);
            string variableName = MigrationEnvironmentVariables.GetRoleVariableName(registration.Owner);
            string? databaseRole = ReadRequiredValue(normalizedVariables, variableName, errors);
            if (databaseRole is null)
            {
                continue;
            }

            try
            {
                _ = registration.CreatePlan(databaseRole);
                rolesByOwner.Add(registration.Owner, databaseRole);
            }
            catch (ArgumentException)
            {
                errors.Add(
                    $"Environment variable '{variableName}' must contain a valid PostgreSQL role identifier.");
            }
        }

        knownOwners.Add(CoreRuntimeMigrations.PublishedReadRoleKey);
        string publishedReadVariable = MigrationEnvironmentVariables.GetRoleVariableName(
            CoreRuntimeMigrations.PublishedReadRoleKey);
        string? publishedReadRole = ReadRequiredValue(
            normalizedVariables,
            publishedReadVariable,
            errors);
        if (publishedReadRole is not null &&
            rolesByOwner.TryGetValue(CoreRuntimeMigrations.Owner, out string? coreOwnerRole))
        {
            try
            {
                _ = CoreRuntimeMigrations.CreatePlan(
                    coreOwnerRole,
                    publishedReadRole);
                rolesByOwner.Add(
                    CoreRuntimeMigrations.PublishedReadRoleKey,
                    publishedReadRole);
            }
            catch (ArgumentException)
            {
                errors.Add(
                    $"Environment variable '{publishedReadVariable}' must contain a valid PostgreSQL role identifier.");
            }
        }

        ValidateUnknownRoleMappings(normalizedVariables, knownOwners, errors);

        if (errors.Count > 0 || connectionString is null)
        {
            return new MigrationConfigurationValidationResult(configuration: null, errors);
        }

        var configuration = new MigrationConfiguration(connectionString, rolesByOwner);
        return new MigrationConfigurationValidationResult(configuration, []);
    }

    private static Dictionary<string, string?> Normalize(
        Dictionary<string, string?> environmentVariables,
        List<string> errors)
    {
        var normalized = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string?> variable in environmentVariables)
        {
            if (!normalized.TryAdd(variable.Key, variable.Value))
            {
                errors.Add(
                    $"Environment variable '{variable.Key}' is configured more than once with different casing.");
            }
        }

        return normalized;
    }

    private static string? ReadRequiredValue(
        Dictionary<string, string?> environmentVariables,
        string variableName,
        List<string> errors)
    {
        if (!environmentVariables.TryGetValue(variableName, out string? value) ||
            string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"Required environment variable '{variableName}' is not configured.");
            return null;
        }

        return value.Trim();
    }

    private static void ValidateUnknownRoleMappings(
        Dictionary<string, string?> environmentVariables,
        HashSet<string> knownOwners,
        List<string> errors)
    {
        foreach (string variableName in environmentVariables.Keys)
        {
            if (!variableName.StartsWith(MigrationEnvironmentVariables.RolePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string owner = variableName[MigrationEnvironmentVariables.RolePrefix.Length..];
            if (!knownOwners.Contains(owner))
            {
                errors.Add(
                    $"Environment variable '{variableName}' refers to unknown migration owner '{owner}'.");
            }
        }
    }
}
