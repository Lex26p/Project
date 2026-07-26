using System.Collections.ObjectModel;
using Npgsql;

namespace Dispatcher.DatabaseMigrator;

public static class DatabaseMigratorApplication
{
    private const int SuccessExitCode = 0;
    private const int InvalidInvocationExitCode = 2;
    private const int PreflightFailureExitCode = 3;
    private const int MigrationFailureExitCode = 4;
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

        return await RunMigrationsAsync(
            standardOutput,
            standardError,
            cancellationToken).ConfigureAwait(false);
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
        "With no options, validates the complete configuration and sequentially applies all migration plans.\n\n" +
        "Configuration names:\n" +
        $"  {MigrationEnvironmentVariables.ConnectionString}\n" +
        $"  {MigrationEnvironmentVariables.RolePrefix}<owner>");

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
            await using NpgsqlDataSource dataSource = CreateDataSource(configuration);
            DatabaseMigrationPreflightResult result = await DatabaseMigrationPreflight.RunAsync(
                dataSource,
                configuration,
                cancellationToken).ConfigureAwait(false);

            if (!result.IsValid)
            {
                await WritePreflightErrorsAsync(result, standardError).ConfigureAwait(false);
                return PreflightFailureExitCode;
            }

            await WritePreflightSuccessAsync(
                result,
                configuration,
                standardOutput,
                includeNoChangesMessage: true).ConfigureAwait(false);
            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await standardError.WriteLineAsync("Database migration was canceled.").ConfigureAwait(false);
            return CancellationExitCode;
        }
        catch (ArgumentException)
        {
            await WriteInvalidConnectionStringAsync(standardError).ConfigureAwait(false);
            return InvalidInvocationExitCode;
        }
        catch (NpgsqlException)
        {
            await WritePreflightConnectionFailureAsync(standardError).ConfigureAwait(false);
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

    private static async Task<int> RunMigrationsAsync(
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
            await using NpgsqlDataSource dataSource = CreateDataSource(configuration);
            DatabaseMigrationPreflightResult preflightResult = await DatabaseMigrationPreflight.RunAsync(
                dataSource,
                configuration,
                cancellationToken).ConfigureAwait(false);
            if (!preflightResult.IsValid)
            {
                await WritePreflightErrorsAsync(preflightResult, standardError).ConfigureAwait(false);
                return PreflightFailureExitCode;
            }

            await WritePreflightSuccessAsync(
                preflightResult,
                configuration,
                standardOutput,
                includeNoChangesMessage: false).ConfigureAwait(false);
            await standardOutput.WriteLineAsync("Applying production migration plans in fixed order.")
                .ConfigureAwait(false);

            DatabaseMigrationResult migrationResult = await DatabaseMigrationCoordinator.ApplyAsync(
                dataSource,
                configuration,
                cancellationToken).ConfigureAwait(false);
            await WriteCompletedOwnersAsync(migrationResult.CompletedOwners, standardOutput).ConfigureAwait(false);

            if (!migrationResult.IsSuccess)
            {
                await standardError.WriteLineAsync(
                    $"Migration failed: owner={migrationResult.FailedOwner}; " +
                    $"schema={migrationResult.FailedSchema}; result=failed.")
                    .ConfigureAwait(false);
                await standardError.WriteLineAsync(
                    $"Reason: {migrationResult.FailureReason}").ConfigureAwait(false);
                await standardError.WriteLineAsync(
                    "No later migration plans were executed.").ConfigureAwait(false);
                return MigrationFailureExitCode;
            }

            await standardOutput.WriteLineAsync("Production database migration succeeded.").ConfigureAwait(false);
            await standardOutput.WriteLineAsync(
                $"Owners processed: {migrationResult.CompletedOwners.Count}.").ConfigureAwait(false);
            await standardOutput.WriteLineAsync(
                $"Migration steps applied: {migrationResult.AppliedStepCount}.").ConfigureAwait(false);
            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await standardError.WriteLineAsync("Database migration was canceled.").ConfigureAwait(false);
            return CancellationExitCode;
        }
        catch (ArgumentException)
        {
            await WriteInvalidConnectionStringAsync(standardError).ConfigureAwait(false);
            return InvalidInvocationExitCode;
        }
        catch (NpgsqlException)
        {
            await WritePreflightConnectionFailureAsync(standardError).ConfigureAwait(false);
            return PreflightFailureExitCode;
        }
        catch (InvalidOperationException)
        {
            await standardError.WriteLineAsync(
                "Database migration failed before plan execution because validation returned an invalid result.")
                .ConfigureAwait(false);
            return PreflightFailureExitCode;
        }
    }

    private static NpgsqlDataSource CreateDataSource(MigrationConfiguration configuration) =>
        new NpgsqlDataSourceBuilder(configuration.ConnectionString).Build();

    private static MigrationConfigurationValidationResult ReadConfiguration()
    {
        Dictionary<string, string?> environmentVariables = MigrationEnvironmentReader.ReadCurrentProcess();
        return MigrationConfigurationParser.Parse(environmentVariables, MigrationCatalog.Registrations);
    }

    private static async Task WriteCompletedOwnersAsync(
        ReadOnlyCollection<DatabaseMigrationOwnerResult> completedOwners,
        TextWriter standardOutput)
    {
        foreach (DatabaseMigrationOwnerResult ownerResult in completedOwners)
        {
            await standardOutput.WriteLineAsync(
                $"owner={ownerResult.Owner}; schema={ownerResult.Schema}; " +
                $"applied_steps={ownerResult.AppliedStepCount}; result=success.")
                .ConfigureAwait(false);
        }
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

    private static async Task WritePreflightErrorsAsync(
        DatabaseMigrationPreflightResult result,
        TextWriter standardError)
    {
        await standardError.WriteLineAsync("PostgreSQL preflight failed:").ConfigureAwait(false);
        foreach (string error in result.Errors)
        {
            await standardError.WriteLineAsync($"- {error}").ConfigureAwait(false);
        }
    }

    private static async Task WritePreflightSuccessAsync(
        DatabaseMigrationPreflightResult result,
        MigrationConfiguration configuration,
        TextWriter standardOutput,
        bool includeNoChangesMessage)
    {
        await standardOutput.WriteLineAsync("PostgreSQL preflight succeeded.").ConfigureAwait(false);
        await standardOutput.WriteLineAsync(
            $"Server major version: {result.ServerMajorVersion}.").ConfigureAwait(false);
        await standardOutput.WriteLineAsync(
            $"Migration owner mappings: {configuration.DatabaseRolesByOwner.Count}.").ConfigureAwait(false);
        await standardOutput.WriteLineAsync(
            $"PostgreSQL roles checked: {result.CheckedRoleCount}.").ConfigureAwait(false);
        if (includeNoChangesMessage)
        {
            await standardOutput.WriteLineAsync(
                "No database schemas or migration steps were changed.").ConfigureAwait(false);
        }
    }

    private static Task WriteInvalidConnectionStringAsync(TextWriter standardError) =>
        standardError.WriteLineAsync(
            "Migration configuration is invalid: the PostgreSQL connection string cannot be parsed (value hidden).");

    private static Task WritePreflightConnectionFailureAsync(TextWriter standardError) =>
        standardError.WriteLineAsync(
            "PostgreSQL preflight failed: unable to connect or execute validation queries.");

    private static bool IsHelpArgument(string argument) =>
        string.Equals(argument, "--help", StringComparison.Ordinal) ||
        string.Equals(argument, "-h", StringComparison.Ordinal);
}
