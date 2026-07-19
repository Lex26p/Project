using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Dispatcher.Persistence;

public sealed record MigrationStep
{
    public MigrationStep(long version, string name, string sql)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A migration version must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        Version = version;
        Name = name.Trim();
        Sql = sql;
    }

    public long Version { get; }

    public string Name { get; }

    public string Sql { get; }
}

public sealed partial class ModuleMigrationPlan
{
    public ModuleMigrationPlan(
        string owner,
        string schema,
        string databaseRole,
        IEnumerable<MigrationStep> steps)
    {
        Owner = ValidateIdentifier(owner, nameof(owner));
        Schema = ValidateIdentifier(schema, nameof(schema));
        DatabaseRole = ValidateIdentifier(databaseRole, nameof(databaseRole));
        ArgumentNullException.ThrowIfNull(steps);

        var orderedSteps = steps.OrderBy(step => step.Version).ToArray();
        if (orderedSteps.Select(step => step.Version).Distinct().Count() != orderedSteps.Length)
        {
            throw new ArgumentException("Migration versions must be unique.", nameof(steps));
        }

        Steps = new ReadOnlyCollection<MigrationStep>(orderedSteps);
    }

    public string Owner { get; }

    public string Schema { get; }

    public string DatabaseRole { get; }

    public IReadOnlyList<MigrationStep> Steps { get; }

    internal static string QuoteIdentifier(string identifier) => $"\"{identifier}\"";

    private static string ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return IdentifierPattern().IsMatch(value)
            ? value
            : throw new ArgumentException(
                "PostgreSQL identifiers must start with a lowercase letter and contain only lowercase letters, digits or underscores.",
                parameterName);
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
