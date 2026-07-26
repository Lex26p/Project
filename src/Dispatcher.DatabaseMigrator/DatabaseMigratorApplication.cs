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
            await standardOutput.WriteLineAsync(
                "Dispatcher.DatabaseMigrator\n\n" +
                "Usage:\n" +
                "  Dispatcher.DatabaseMigrator\n\n" +
                "The executable project is installed. Migration execution will be enabled in a later C01 step.")
                .ConfigureAwait(false);

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

    private static bool IsHelpArgument(string argument) =>
        string.Equals(argument, "--help", StringComparison.Ordinal) ||
        string.Equals(argument, "-h", StringComparison.Ordinal);
}
