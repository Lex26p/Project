using System.Collections.ObjectModel;
using Npgsql;

namespace Dispatcher.DatabaseMigrator;

public static class DatabaseMigratorApplication
{
    private const int SuccessExitCode = 0;
    private const int InvalidInvocationExitCode = 2;
    private const int PreflightFailureExitCode = 3;
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

        if (arguments.Count == 1 && string.Equals(arguments[0], "--validate-config", StringComparison.Ordinal))
        {
            return await ValidateConfigurationAsync(standardOutput, standardError).ConfigureAwait(false);
        }

        if (arguments.Count == 1 && string.Equals(arguments[0], "--preflight", StringComparison.Ordinal))
        {
            return await RunPreflightAsync(
                standardOutput,
                standardError,
                cancellationToken).ConfigureAwait(false);
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
        "  Dispatcher.DatabaseMigrator --list-plans\n" +
        "  Dispatcher.DatabaseMigrator --validate-config\n" +
        "  Dispatcher.DatabaseMigrator --preflight\n\n" +
        "Options:\n" +
        "  --list-plans       Validate and print the fixed production migration catalog.\n" +
        "  --validate-config  Validate environment configuration without connecting to PostgreSQL.\n" +
        "  --preflight        Validate PostgreSQL version, roles and SET ROLE permissions without migrations.\n\n" +
        "Configuration names:\n" +
        $"  {MigrationEnvironmentVariables.ConnectionString}\n" +
        $"  {MigrationEnvironmentVariables.RolePrefix}<owner>\n\n" +
        "Migration execution will be enabled in a later C01 step.");

    private static async Task WritePlanCatalogAsync(TextWriter standardOutput)
    {
        ReadOnlyCollection<MigrationPlanRegistration> registrations = MigrationCatalog.Registrations;
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

    private static async Task<int> ValidateConfigurationAsync(
        TextWriter standardOutput,
        TextWriter standardError)
    {
        MigrationConfigurationValidationResult result = ReadConfiguration();
        if (!result.IsValid)
        {
            await WriteConfigurationErrorsAsync(result, standardError).ConfigureAwait(false);
            return InvalidInvocationExitCode;
        }

        MigrationConfiguration configuration = result.Configuration!;
        await standardOutput.WriteLineAsync("Migration configuration is valid.").ConfigureAwait(false);
        await standardOutput.WriteLineAsync("Connection string: configured (value hidden).").ConfigureAwait(false);
        await standardOutput.WriteLineAsync(
            $"PostgreSQL role mappings: {configuration.DatabaseRolesByOwner.Count}.").ConfigureAwait(false);
        return SuccessExitCode;
    }

    private static async Task<int> RunPreflightAsync(
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        MigrationConfigurationValidationResult configurationResult = ReadConfiguration();
        if (!configurationResult.IsValid)
        {
            await WriteConfigurationErrorsAsync(configurationResult, standardError).ConfigureAwait(false);
            return InvalidInvocationExitCode;
        }

        MigrationConfiguration configuration = configurationResult.Configuration!;
        try
        {
            await using NpgsqlDataSource dataSource =
                new NpgsqlDataSourceBuilder(configuration.ConnectionString).Build();
            DatabaseMigrationPreflightResult result = await DatabaseMigrationPreflight.RunAsync(
                dataSource,
                configuration,
                cancellationToken).ConfigureAwait(false);

            if (!result.IsValid)
            {
                await standardError.WriteLineAsync("PostgreSQL preflight failed:").ConfigureAwait(false);
                foreach (string error in result.Errors)
                {
                    await standardError.WriteLineAsync($"- {error}").ConfigureAwait(false);
                }

                return PreflightFailureExitCode;
            }

            await standardOutput.WriteLineAsync("PostgreSQL preflight succeeded.").ConfigureAwait(false);
            await standardOutput.WriteLineAsync(
                $"Server major version: {result.ServerMajorVersion}.").ConfigureAwait(false);
            await standardOutput.WriteLineAsync(
                $"Migration owner mappings: {configuration.DatabaseRolesByOwner.Count}.").ConfigureAwait(false);
            await standardOutput.WriteLineAsync(
                $"PostgreSQL roles checked: {result.CheckedRoleCount}.").ConfigureAwait(false);
            await standardOutput.WriteLineAsync(
                "No database schemas or migration steps were changed.").ConfigureAwait(false);
            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await standardError.WriteLineAsync("Database migration was canceled.").ConfigureAwait(false);
            return CancellationExitCode;
        }
        catch (ArgumentException)
        {
            await standardError.WriteLineAsync(
                "Migration configuration is invalid: the PostgreSQL connection string cannot be parsed (value hidden).")
                .ConfigureAwait(false);
            return InvalidInvocationExitCode;
        }
        catch (NpgsqlException)
        {
            await standardError.WriteLineAsync(
                "PostgreSQL preflight failed: unable to connect or execute validation queries.")
                .ConfigureAwait(false);
            return PreflightFailureExitCode;
        }
        catch (InvalidOperationException)
        {
            await standardError.WriteLineAsync(
                "PostgreSQL preflight failed: the server returned an invalid validation result.")
                .ConfigureAwait(false);
            return PreflightFailureExitCode;
        }
    }

    private static MigrationConfigurationValidationResult ReadConfiguration()
    {
        Dictionary<string, string?> environmentVariables = MigrationEnvironmentReader.ReadCurrentProcess();
        return MigrationConfigurationParser.Parse(environmentVariables, MigrationCatalog.Registrations);
    }

    private static async Task WriteConfigurationErrorsAsync(
        MigrationConfigurationValidationResult result,
        TextWriter standardError)
    {
        await standardError.WriteLineAsync("Migration configuration is invalid:").ConfigureAwait(false);
        foreach (string error in result.Errors)
        {
            await standardError.WriteLineAsync($"- {error}").ConfigureAwait(false);
        }
    }

    private static bool IsHelpArgument(string argument) =>
        string.Equals(argument, "--help", StringComparison.Ordinal) ||
        string.Equals(argument, "-h", StringComparison.Ordinal);
}
