namespace Dispatcher.DatabaseMigrator;

public static class DatabaseMigratorApplication
{
    private const int SuccessExitCode = 0;
    private const int InvalidInvocationExitCode = 2;
    private const int CancellationExitCode = 130;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (cancellationToken.IsCancellationRequested)
        {
            await standardError.WriteLineAsync("Database migration was canceled.").ConfigureAwait(false);
            return CancellationExitCode;
        }

        if (arguments.Count == 1 && IsHelpArgument(arguments[0]))
        {
            await WriteHelpAsync(standardOutput).ConfigureAwait(false);
            return SuccessExitCode;
        }

        if (arguments.Count == 1 && string.Equals(arguments[0], "--list-plans", StringComparison.Ordinal))
        {
            await WritePlanCatalogAsync(standardOutput).ConfigureAwait(false);
            return SuccessExitCode;
        }

        if (arguments.Count > 0)
        {
            await standardError.WriteLineAsync(
                "Unsupported command-line arguments. Use --help to display usage.")
                .ConfigureAwait(false);

            return InvalidInvocationExitCode;
        }

        await standardError.WriteLineAsync(
            "Migration execution is not enabled in this intermediate build. Use --help to display usage.")
            .ConfigureAwait(false);

        return InvalidInvocationExitCode;
    }

    private static Task WriteHelpAsync(TextWriter standardOutput) => standardOutput.WriteLineAsync(
        "Dispatcher.DatabaseMigrator\n\n" +
        "Usage:\n" +
        "  Dispatcher.DatabaseMigrator\n" +
        "  Dispatcher.DatabaseMigrator --list-plans\n\n" +
        "Options:\n" +
        "  --list-plans  Validate and print the fixed production migration catalog.\n\n" +
        "Migration execution will be enabled in a later C01 step.");

    private static async Task WritePlanCatalogAsync(TextWriter standardOutput)
    {
        IReadOnlyList<MigrationPlanRegistration> registrations = MigrationCatalog.Registrations;
        await standardOutput.WriteLineAsync(
            $"Production migration catalog: {registrations.Count} plans.").ConfigureAwait(false);

        for (var index = 0; index < registrations.Count; index++)
        {
            MigrationPlanRegistration registration = registrations[index];
            await standardOutput.WriteLineAsync(
                $"{index + 1}. owner={registration.Owner}; schema={registration.Schema}")
                .ConfigureAwait(false);
        }
    }

    private static bool IsHelpArgument(string argument) =>
        string.Equals(argument, "--help", StringComparison.Ordinal) ||
        string.Equals(argument, "-h", StringComparison.Ordinal);
}
