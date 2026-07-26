using System.Collections.ObjectModel;

namespace Dispatcher.DatabaseMigrator;

public sealed class MigrationConfigurationValidationResult
{
    internal MigrationConfigurationValidationResult(
        MigrationConfiguration? configuration,
        List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if ((configuration is null) == (errors.Count == 0))
        {
            throw new ArgumentException(
                "A validation result must contain either a configuration or at least one error.",
                nameof(errors));
        }

        Configuration = configuration;
        Errors = Array.AsReadOnly(errors.ToArray());
    }

    public MigrationConfiguration? Configuration { get; }

    public ReadOnlyCollection<string> Errors { get; }

    public bool IsValid => Configuration is not null;
}
