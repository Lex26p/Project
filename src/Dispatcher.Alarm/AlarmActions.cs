using Dispatcher.Core;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Alarm;

public static class AlarmPermissions
{
    public static PermissionCode Acknowledge { get; } = PermissionCode.From("alarm.occurrence.acknowledge");

    public static PermissionCode Assign { get; } = PermissionCode.From("alarm.occurrence.assign");

    public static PermissionCode Shelve { get; } = PermissionCode.From("alarm.occurrence.shelve");
}

public sealed record AlarmMaintenanceConstraintSnapshot
{
    public AlarmMaintenanceConstraintSnapshot(
        AlarmOccurrenceId occurrenceId,
        StateVersion version,
        bool acknowledgementAllowed,
        bool assignmentAllowed,
        bool shelvingAllowed,
        DateTimeOffset? windowEndsAt)
    {
        _ = occurrenceId.Value;
        if (!version.IsDefined)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        if (windowEndsAt is not null && windowEndsAt.Value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Maintenance constraint timestamps must be UTC.", nameof(windowEndsAt));
        }

        OccurrenceId = occurrenceId;
        Version = version;
        AcknowledgementAllowed = acknowledgementAllowed;
        AssignmentAllowed = assignmentAllowed;
        ShelvingAllowed = shelvingAllowed;
        WindowEndsAt = windowEndsAt;
    }

    public AlarmOccurrenceId OccurrenceId { get; }

    public StateVersion Version { get; }

    public bool AcknowledgementAllowed { get; }

    public bool AssignmentAllowed { get; }

    public bool ShelvingAllowed { get; }

    public DateTimeOffset? WindowEndsAt { get; }

    public static AlarmMaintenanceConstraintSnapshot Unrestricted(AlarmOccurrenceId occurrenceId) => new(
        occurrenceId,
        StateVersion.Initial,
        acknowledgementAllowed: true,
        assignmentAllowed: true,
        shelvingAllowed: true,
        windowEndsAt: null);
}

public sealed record AcknowledgeAlarmRequest(
    RuntimeScopeId ScopeId,
    PointId PointId,
    AlarmOccurrenceId OccurrenceId,
    StateVersion ExpectedVersion,
    string IdempotencyKey,
    AlarmMaintenanceConstraintSnapshot Constraint);

public sealed record AssignAlarmRequest(
    RuntimeScopeId ScopeId,
    PointId PointId,
    AlarmOccurrenceId OccurrenceId,
    Guid AssignedTo,
    StateVersion ExpectedVersion,
    string IdempotencyKey,
    AlarmMaintenanceConstraintSnapshot Constraint);

public sealed record ShelveAlarmRequest(
    RuntimeScopeId ScopeId,
    PointId PointId,
    AlarmOccurrenceId OccurrenceId,
    DateTimeOffset ShelvedUntil,
    string Reason,
    StateVersion ExpectedVersion,
    string IdempotencyKey,
    AlarmMaintenanceConstraintSnapshot Constraint);

public enum AlarmActionDisposition
{
    Applied = 1,
    Replay = 2,
}

public sealed record AlarmActionResult(
    AlarmOccurrenceSnapshot Occurrence,
    AlarmActionDisposition Disposition);

public enum AlarmActionCompletion
{
    Applied = 1,
    Replayed = 2,
    Unknown = 3,
}

public sealed record AlarmActionCompletionResult(
    AlarmActionCompletion Completion,
    AlarmOccurrenceSnapshot? Occurrence,
    string IdempotencyKey);

public sealed record AlarmActionCommitHook(Action<AlarmOccurrenceSnapshot> AfterCommit);
