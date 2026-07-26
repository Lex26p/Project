using System.Collections.ObjectModel;

namespace Dispatcher.DatabaseMigrator;

public sealed class DatabaseMigrationOwnerResult
{
    public DatabaseMigrationOwnerResult(string owner, string schema, int appliedStepCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (appliedStepCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(appliedStepCount),
                "The applied migration step count cannot be negative.");
        }

        Owner = owner;
        Schema = schema;
        AppliedStepCount = appliedStepCount;
    }

    public string Owner { get; }

    public string Schema { get; }

    public int AppliedStepCount { get; }
}

public sealed class DatabaseMigrationResult
{
    internal DatabaseMigrationResult(
        List<DatabaseMigrationOwnerResult> completedOwners,
        string? failedOwner,
        string? failedSchema,
        string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(completedOwners);

        bool hasFailure = failedOwner is not null || failedSchema is not null || failureReason is not null;
        if (hasFailure &&
            (string.IsNullOrWhiteSpace(failedOwner) ||
             string.IsNullOrWhiteSpace(failedSchema) ||
             string.IsNullOrWhiteSpace(failureReason)))
        {
            throw new InvalidOperationException("Failure details must be either complete or absent.");
        }

        CompletedOwners = Array.AsReadOnly(completedOwners.ToArray());
        FailedOwner = failedOwner;
        FailedSchema = failedSchema;
        FailureReason = failureReason;
        AppliedStepCount = completedOwners.Sum(static owner => owner.AppliedStepCount);
    }

    public ReadOnlyCollection<DatabaseMigrationOwnerResult> CompletedOwners { get; }

    public string? FailedOwner { get; }

    public string? FailedSchema { get; }

    public string? FailureReason { get; }

    public int AppliedStepCount { get; }

    public bool IsSuccess => FailedOwner is null;
}
