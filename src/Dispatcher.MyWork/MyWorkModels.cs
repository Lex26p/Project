using Dispatcher.Incidents;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;

namespace Dispatcher.MyWork;

public sealed record WorkAssignmentProjection(
    string SourceOwner,
    string SourceKind,
    Guid SourceItemId,
    StateVersion SourceVersion,
    PersonId AssignedPersonId,
    string Summary,
    string State,
    string Route,
    IReadOnlyList<PermissionCode> RequiredPermissions,
    DateTimeOffset UpdatedAt)
{
    public static WorkAssignmentProjection FromIncidentTask(IncidentTaskSnapshot task) => new(
        "incidents", "incident-task", task.TaskId.Value, task.Version, task.AssignedPersonId,
        task.Summary, task.State.ToString(),
        $"/incidents/{task.IncidentId.Value}/tasks/{task.TaskId.Value}",
        [IncidentPermissions.Read], task.UpdatedAt);
}

public sealed record MyWorkUserContext(SessionSnapshot Session, PersonId PersonId);

public static class MyWorkPermissions
{
    public static PermissionCode Read { get; } = PermissionCode.From("my-work.read");
}
