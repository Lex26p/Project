using Dispatcher.Incidents;
using Dispatcher.Maintenance;
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

    public static WorkAssignmentProjection FromMaintenanceWorkOrder(MaintenanceWorkOrderSnapshot workOrder) => new(
        "maintenance",
        "work-order",
        workOrder.WorkOrderId.Value,
        workOrder.Version,
        workOrder.AssignedPersonId,
        workOrder.Summary,
        workOrder.State.ToString(),
        $"/maintenance/work-orders/{workOrder.WorkOrderId.Value}",
        [MaintenancePermissions.Read(workOrder.ScopeId)],
        workOrder.UpdatedAt);
}

public sealed record MyWorkUserContext(SessionSnapshot Session, PersonId PersonId);

public static class MyWorkPermissions
{
    public static PermissionCode Read { get; } = PermissionCode.From("my-work.read");
}
