namespace Dispatcher.Web;

public sealed record IncidentSummaryPayload(
    Guid IncidentId,
    string Summary,
    Guid CoordinatorPersonId,
    ulong Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<IncidentSourceLinkPayload> Sources);

public sealed record IncidentSourceLinkPayload(
    Guid LinkId,
    Guid EventId,
    Guid OccurrenceId,
    Guid ScopeId,
    Guid PointId,
    string Route);

public sealed record IncidentTaskPayload(
    Guid TaskId,
    Guid IncidentId,
    string Summary,
    Guid AssignedPersonId,
    string State,
    ulong Version,
    DateTimeOffset? DueAt,
    string? LastTransitionReason,
    DateTimeOffset UpdatedAt);

public sealed record CreateIncidentWorkflowPayload(
    IncidentSummaryPayload Incident,
    IncidentTaskPayload? Task);

public sealed record MyWorkItemPayload(
    string SourceOwner,
    string SourceKind,
    Guid SourceItemId,
    ulong SourceVersion,
    Guid AssignedPersonId,
    string Summary,
    string State,
    string Route,
    DateTimeOffset? DueAt,
    string? LastTransitionReason,
    DateTimeOffset UpdatedAt);

public sealed record MyWorkCountersPayload(
    int Overdue,
    int Today,
    int RequiresDecision,
    int AssignedToMe);

public sealed record TransferCandidatePayload(
    Guid PersonId,
    string DisplayName,
    string? Title);

public sealed record CreateIncidentFromEventInput(
    Guid IncidentId,
    string Summary,
    Guid? CoordinatorPersonId,
    bool CreateTask,
    Guid? TaskId,
    string? TaskSummary,
    Guid? AssignedPersonId,
    DateTimeOffset? DueAt,
    string IdempotencyKey);

public sealed record TransitionMyWorkTaskInput(
    ulong ExpectedVersion,
    string IdempotencyKey,
    Guid? TransferToPersonId,
    string? Reason);

public enum IncidentMyWorkApiStatus
{
    Available = 1,
    SessionExpired = 2,
    Forbidden = 3,
    Invalid = 4,
    NotFound = 5,
    Conflict = 6,
    Unavailable = 7,
}

public sealed record IncidentMyWorkApiResult<TPayload>(
    IncidentMyWorkApiStatus Status,
    TPayload? Payload);
