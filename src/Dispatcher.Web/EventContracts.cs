namespace Dispatcher.Web;

public sealed record EventPayload(
    Guid EventId,
    ulong Position,
    Guid PointId,
    Guid OccurrenceId,
    ulong SourceConditionVersion,
    string Priority,
    string Kind,
    DateTimeOffset OccurredAt,
    DateTimeOffset AcceptedAt);

public sealed record EventPagePayload(
    IReadOnlyList<EventPayload> Events,
    ulong? UpperBound,
    ulong? NextAfter,
    ulong? NextUpper);

public sealed record EventDispatcherCounters(
    long EventCount,
    long ActiveOccurrenceCount,
    long UnacknowledgedOccurrenceCount);

public sealed record OccurrencePayload(
    ulong ProjectionVersion,
    Guid OccurrenceId,
    Guid PointId,
    string Priority,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    string ConditionState,
    DateTimeOffset? ConditionPendingSince,
    DateTimeOffset? ConditionActiveSince,
    DateTimeOffset? ConditionClearedAt,
    ulong ConditionVersion,
    string AcknowledgementState,
    Guid? AcknowledgedBy,
    DateTimeOffset? AcknowledgedAt,
    ulong AcknowledgementVersion,
    Guid? AssignedTo,
    DateTimeOffset? AssignedAt,
    ulong AssignmentVersion,
    DateTimeOffset? ShelvedUntil,
    string? ShelvingReason,
    ulong ShelvingVersion,
    bool IsSuppressed,
    string? SuppressionReason,
    ulong SuppressionVersion,
    bool CanAcknowledge,
    bool CanAssign,
    bool CanShelve)
{
    public bool IsActive =>
        ClosedAt is null &&
        ConditionState is
            "Active" or "PendingClear";

    public bool IsShelved(
        DateTimeOffset now) =>
        ShelvedUntil is { } until &&
        until > now;
}

public sealed record OccurrenceSnapshotPayload(
    ulong Cursor,
    IReadOnlyList<OccurrencePayload> Occurrences);

public sealed record OccurrenceFeedPayload(
    string Kind,
    ulong From,
    ulong To,
    IReadOnlyList<OccurrencePayload> Changes);

public sealed record AlarmFacetActionRequest(
    Guid PointId,
    ulong ExpectedVersion,
    string IdempotencyKey);

public sealed record AssignAlarmPayload(
    Guid PointId,
    Guid AssignedTo,
    ulong ExpectedVersion,
    string IdempotencyKey);

public sealed record ShelveAlarmPayload(
    Guid PointId,
    DateTimeOffset ShelvedUntil,
    string Reason,
    ulong ExpectedVersion,
    string IdempotencyKey);

public sealed record AlarmActionPayload(
    string Completion,
    string IdempotencyKey,
    Guid? OccurrenceId,
    Guid? PointId,
    string? Priority,
    string? DashboardBindingKey,
    string? EquipmentHref);

public enum EventApiStatus
{
    Available = 1,
    SessionExpired = 2,
    Forbidden = 3,
    Conflict = 4,
    Unavailable = 5,
}

public sealed record EventApiResult<TPayload>(
    EventApiStatus Status,
    TPayload? Payload);

public sealed record EventFilter(
    string Priority,
    string Condition,
    string Acknowledgement,
    string Assignment,
    string Special,
    string Text)
{
    public static EventFilter Default { get; } =
        new(
            "All",
            "All",
            "All",
            "All",
            "All",
            string.Empty);
}

public sealed record EventViewCounters(
    int Total,
    int Active,
    int Unacknowledged,
    int Shelved);
