using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;

namespace Dispatcher.Incidents;

public readonly record struct IncidentId
{
    private readonly CanonicalId<IncidentId> value;
    private IncidentId(CanonicalId<IncidentId> value) => this.value = value;
    public Guid Value => value.Value;
    public static IncidentId New() => new(CanonicalId.New<IncidentId>());
    public static IncidentId From(Guid value) => new(CanonicalId.From<IncidentId>(value));
}

public readonly record struct IncidentSourceLinkId
{
    private readonly CanonicalId<IncidentSourceLinkId> value;
    private IncidentSourceLinkId(CanonicalId<IncidentSourceLinkId> value) => this.value = value;
    public Guid Value => value.Value;
    public static IncidentSourceLinkId New() => new(CanonicalId.New<IncidentSourceLinkId>());
    public static IncidentSourceLinkId From(Guid value) => new(CanonicalId.From<IncidentSourceLinkId>(value));
}

public readonly record struct IncidentTaskId
{
    private readonly CanonicalId<IncidentTaskId> value;
    private IncidentTaskId(CanonicalId<IncidentTaskId> value) => this.value = value;
    public Guid Value => value.Value;
    public static IncidentTaskId New() => new(CanonicalId.New<IncidentTaskId>());
    public static IncidentTaskId From(Guid value) => new(CanonicalId.From<IncidentTaskId>(value));
}

public sealed record IncidentSourceLink(
    IncidentSourceLinkId LinkId,
    OperationalEventId EventId,
    AlarmOccurrenceId OccurrenceId,
    RuntimeScopeId ScopeId,
    PointId PointId,
    string Route,
    IReadOnlyList<PermissionCode> RequiredPermissions);

public sealed record IncidentSnapshot(
    IncidentId IncidentId,
    string Summary,
    PersonId CoordinatorPersonId,
    StateVersion Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<IncidentSourceLink> SourceLinks);

public enum IncidentTaskState { Offered = 1, Accepted = 2, Returned = 3 }

public sealed record IncidentTaskSnapshot(
    IncidentTaskId TaskId,
    IncidentId IncidentId,
    string Summary,
    PersonId AssignedPersonId,
    IncidentTaskState State,
    StateVersion Version,
    DateTimeOffset UpdatedAt);

public sealed record CreateIncidentRequest(
    IncidentId IncidentId,
    string Summary,
    PersonId CoordinatorPersonId,
    string IdempotencyKey);

public sealed record LinkIncidentEventRequest(
    IncidentId IncidentId,
    OperationalEventRecord Event,
    StateVersion ExpectedVersion,
    string IdempotencyKey);

public sealed record CreateIncidentTaskRequest(
    IncidentTaskId TaskId,
    IncidentId IncidentId,
    string Summary,
    PersonId AssignedPersonId,
    StateVersion ExpectedIncidentVersion,
    string IdempotencyKey);

public sealed record TransitionIncidentTaskRequest(
    IncidentTaskId TaskId,
    StateVersion ExpectedVersion,
    string IdempotencyKey,
    PersonId? TransferTo = null);

public enum IncidentCommandDisposition { Applied = 1, Replay = 2 }
public sealed record IncidentCommandResult<T>(T Value, IncidentCommandDisposition Disposition);
public sealed record IncidentUserContext(SessionSnapshot Session, PersonId PersonId);

public static class IncidentPermissions
{
    public static PermissionCode Create { get; } = PermissionCode.From("incident.create");
    public static PermissionCode Read { get; } = PermissionCode.From("incident.read");
    public static PermissionCode LinkSource { get; } = PermissionCode.From("incident.source.link");
    public static PermissionCode ManageTask { get; } = PermissionCode.From("incident.task.manage");
    public static PermissionCode TransitionTask { get; } = PermissionCode.From("incident.task.transition");
}
