using Dispatcher.Core;
using Dispatcher.Persistence;
using Npgsql;

namespace Dispatcher.DatabaseMigrator;

public static class DatabaseMigrationCoordinator
{
    public static async Task<DatabaseMigrationResult> ApplyAsync(
        NpgsqlDataSource dataSource,
        MigrationConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configuration);

        var completedOwners = new List<DatabaseMigrationOwnerResult>(MigrationCatalog.ExpectedPlanCount);
        foreach (MigrationPlanRegistration registration in MigrationCatalog.Registrations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string databaseRole = configuration.GetDatabaseRole(registration.Owner);
                ModuleMigrationPlan plan = registration.Owner == CoreRuntimeMigrations.Owner
                    ? CoreRuntimeMigrations.CreatePlan(
                        databaseRole,
                        configuration.GetDatabaseRole(
                            CoreRuntimeMigrations.PublishedReadRoleKey))
                    : registration.CreatePlan(databaseRole);
                int appliedStepCount = await PostgresMigrationRunner.ApplyAsync(
                    dataSource,
                    plan,
                    cancellationToken).ConfigureAwait(false);
                completedOwners.Add(new DatabaseMigrationOwnerResult(
                    registration.Owner,
                    registration.Schema,
                    appliedStepCount));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidOperationException exception)
            {
                return CreateFailure(
                    completedOwners,
                    registration,
                    DescribeInvalidMigrationState(exception));
            }
            catch (NpgsqlException)
            {
                return CreateFailure(
                    completedOwners,
                    registration,
                    "PostgreSQL rejected or interrupted the migration plan.");
            }
            catch (ArgumentException)
            {
                return CreateFailure(
                    completedOwners,
                    registration,
                    "The migration plan contains invalid configuration or identifiers.");
            }
        }

        return new DatabaseMigrationResult(completedOwners, null, null, null);
    }

    private static DatabaseMigrationResult CreateFailure(
        List<DatabaseMigrationOwnerResult> completedOwners,
        MigrationPlanRegistration registration,
        string failureReason) =>
        new(
            completedOwners,
            registration.Owner,
            registration.Schema,
            failureReason);

    private static string DescribeInvalidMigrationState(InvalidOperationException exception)
    {
        const string checksumSuffix = "has a different checksum.";
        const string ownershipFragment = "is not owned by expected role";

        if (exception.Message.EndsWith(checksumSuffix, StringComparison.Ordinal))
        {
            return "A stored migration checksum does not match the current SQL.";
        }

        if (exception.Message.Contains(ownershipFragment, StringComparison.Ordinal))
        {
            return "The schema owner does not match the configured PostgreSQL role.";
        }

        return "The migration state failed validation.";
    }
}
