using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;

namespace Dispatcher.Maintenance;

public readonly record struct MaintenanceRequestId
{
    private readonly CanonicalId<MaintenanceRequestId> value;
    private MaintenanceRequestId(CanonicalId<MaintenanceRequestId> value) => this.value = value;
    public Guid Value => value.Value;
    public static MaintenanceRequestId New() => new(CanonicalId.New<MaintenanceRequestId>());
    public static MaintenanceRequestId From(Guid value) => new(CanonicalId.From<MaintenanceRequestId>(value));
}

public readonly record struct MaintenanceDefectId
{
    private readonly CanonicalId<MaintenanceDefectId> value;
    private MaintenanceDefectId(CanonicalId<MaintenanceDefectId> value) => this.value = value;
    public Guid Value => value.Value;
    public static MaintenanceDefectId New() => new(CanonicalId.New<MaintenanceDefectId>());
    public static MaintenanceDefectId From(Guid value) => new(CanonicalId.From<MaintenanceDefectId>(value));
}

public readonly record struct MaintenanceWorkOrderId
{
    private readonly CanonicalId<MaintenanceWorkOrderId> value;
    private MaintenanceWorkOrderId(CanonicalId<MaintenanceWorkOrderId> value) => this.value = value;
    public Guid Value => value.Value;
    public static MaintenanceWorkOrderId New() => new(CanonicalId.New<MaintenanceWorkOrderId>());
    public static MaintenanceWorkOrderId From(Guid value) => new(CanonicalId.From<MaintenanceWorkOrderId>(value));
}

public readonly record struct WorkOrderChecklistItemId
{
    private readonly CanonicalId<WorkOrderChecklistItemId> value;
    private WorkOrderChecklistItemId(CanonicalId<WorkOrderChecklistItemId> value) => this.value = value;
    public Guid Value => value.Value;
    public static WorkOrderChecklistItemId New() => new(CanonicalId.New<WorkOrderChecklistItemId>());
    public static WorkOrderChecklistItemId From(Guid value) => new(CanonicalId.From<WorkOrderChecklistItemId>(value));
}

public enum MaintenanceRequestState { Submitted = 1, Approved = 2, Converted = 3 }
public enum MaintenanceDefectState { Reported = 1, Confirmed = 2, Converted = 3 }
public enum MaintenanceWorkOrderState
{
    Overdue = 1,
    Assigned = 2,
    Accepted = 3,
    InProgress = 4,
    PendingAcceptance = 5,
    Completed = 6,
}
public enum MaintenanceWorkSourceKind { Request = 1, Defect = 2, Forecast = 3 }

public sealed record MaintenanceEventSourceLink(
    OperationalEventId EventId,
    AlarmOccurrenceId OccurrenceId,
    RuntimeScopeId ScopeId,
    PointId PointId,
    string Route,
    IReadOnlyList<PermissionCode> RequiredPermissions);

public sealed record MaintenanceRequestSnapshot(
    MaintenanceRequestId RequestId,
    MaintenanceAssetId AssetId,
    FacilityScopeId ScopeId,
    string Summary,
    MaintenanceRequestState State,
    StateVersion Version,
    MaintenanceEventSourceLink? EventSource,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MaintenanceDefectSnapshot(
    MaintenanceDefectId DefectId,
    MaintenanceAssetId AssetId,
    FacilityScopeId ScopeId,
    string Summary,
    MaintenanceDefectState State,
    StateVersion Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MaintenanceRequestQuery(
    FacilityScopeId ScopeId,
    int PageSize,
    MaintenanceRequestState? State = null,
    MaintenanceAssetId? AssetId = null,
    string? Search = null,
    DateTimeOffset? AfterCreatedAt = null,
    MaintenanceRequestId? AfterRequestId = null);

public sealed record MaintenanceRequestPage(
    IReadOnlyList<MaintenanceRequestSnapshot> Requests,
    DateTimeOffset? NextCreatedAt,
    MaintenanceRequestId? NextRequestId);

public sealed record MaintenanceDefectQuery(
    FacilityScopeId ScopeId,
    int PageSize,
    MaintenanceDefectState? State = null,
    MaintenanceAssetId? AssetId = null,
    string? Search = null,
    DateTimeOffset? AfterCreatedAt = null,
    MaintenanceDefectId? AfterDefectId = null);

public sealed record MaintenanceDefectPage(
    IReadOnlyList<MaintenanceDefectSnapshot> Defects,
    DateTimeOffset? NextCreatedAt,
    MaintenanceDefectId? NextDefectId);

public sealed record WorkOrderSafetyFields(
    bool PermitRequired,
    bool IsolationRequired,
    string? Instructions,
    DateTimeOffset? AcknowledgedAt);

public sealed record WorkOrderChecklistItem(
    WorkOrderChecklistItemId ItemId,
    string Description,
    bool Mandatory,
    DateTimeOffset? CompletedAt,
    PersonId? CompletedBy);

public sealed record MaintenanceWorkOrderSnapshot(
    MaintenanceWorkOrderId WorkOrderId,
    MaintenanceAssetId AssetId,
    FacilityScopeId ScopeId,
    MaintenanceWorkSourceKind SourceKind,
    Guid SourceId,
    string Summary,
    PersonId AssignedPersonId,
    MaintenanceWorkOrderState State,
    WorkOrderSafetyFields Safety,
    StateVersion Version,
    IReadOnlyList<WorkOrderChecklistItem> Checklist,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MaintenanceWorkOrderQuery(
    FacilityScopeId ScopeId,
    int PageSize,
    MaintenanceWorkOrderState? State = null,
    MaintenanceAssetId? AssetId = null,
    PersonId? AssignedPersonId = null,
    string? Search = null,
    DateTimeOffset? AfterCreatedAt = null,
    MaintenanceWorkOrderId? AfterWorkOrderId = null);

public sealed record MaintenanceWorkOrderPage(
    IReadOnlyList<MaintenanceWorkOrderSnapshot> WorkOrders,
    DateTimeOffset? NextCreatedAt,
    MaintenanceWorkOrderId? NextWorkOrderId);

public sealed record MaintenanceOverviewSnapshot(
    int Overdue,
    int DueToday,
    int RequiresAssignment,
    int InProgress,
    int PendingAcceptance,
    int SafetyAttention);

public sealed record CreateMaintenanceRequest(
    MaintenanceRequestId RequestId,
    MaintenanceAssetId AssetId,
    string Summary,
    string IdempotencyKey);

public sealed record CreateMaintenanceRequestFromEvent(
    MaintenanceRequestId RequestId,
    MaintenanceAssetId AssetId,
    string Summary,
    OperationalEventRecord Event,
    string IdempotencyKey);

public sealed record ApproveMaintenanceRequest(
    MaintenanceRequestId RequestId,
    StateVersion ExpectedVersion,
    string IdempotencyKey);

public sealed record CreateMaintenanceDefect(
    MaintenanceDefectId DefectId,
    MaintenanceAssetId AssetId,
    string Summary,
    string IdempotencyKey);

public sealed record ConfirmMaintenanceDefect(
    MaintenanceDefectId DefectId,
    StateVersion ExpectedVersion,
    string IdempotencyKey);

public sealed record NewWorkOrderChecklistItem(
    WorkOrderChecklistItemId ItemId,
    string Description,
    bool Mandatory);

public sealed record CreateWorkOrderFromRequest(
    MaintenanceWorkOrderId WorkOrderId,
    MaintenanceRequestId RequestId,
    string Summary,
    PersonId AssignedPersonId,
    WorkOrderSafetyFields Safety,
    IReadOnlyCollection<NewWorkOrderChecklistItem> Checklist,
    StateVersion ExpectedSourceVersion,
    string IdempotencyKey);

public sealed record CreateWorkOrderFromDefect(
    MaintenanceWorkOrderId WorkOrderId,
    MaintenanceDefectId DefectId,
    string Summary,
    PersonId AssignedPersonId,
    WorkOrderSafetyFields Safety,
    IReadOnlyCollection<NewWorkOrderChecklistItem> Checklist,
    StateVersion ExpectedSourceVersion,
    string IdempotencyKey);

public sealed record CreateWorkOrderFromForecast(
    MaintenanceWorkOrderId WorkOrderId,
    MaintenanceForecastObligationId ObligationId,
    MaintenanceAssetId AssetId,
    FacilityScopeId ScopeId,
    string Summary,
    PersonId AssignedPersonId,
    WorkOrderSafetyFields Safety,
    IReadOnlyCollection<NewWorkOrderChecklistItem> Checklist,
    string IdempotencyKey);

public sealed record MaintenanceTimelineEntry(
    string EntityKind,
    Guid EntityId,
    string Action,
    StateVersion ResultingVersion,
    DateTimeOffset ChangedAt);

public sealed record MaintenanceCrossLink(
    string Route,
    IReadOnlyList<PermissionCode> RequiredPermissions);

public static class MaintenanceCrossLinks
{
    public static MaintenanceCrossLink SourceFor(MaintenanceWorkOrderSnapshot workOrder) =>
        workOrder.SourceKind switch
        {
            MaintenanceWorkSourceKind.Request => new(
                $"/maintenance/requests/{workOrder.SourceId}",
                [MaintenancePermissions.Read(workOrder.ScopeId)]),
            MaintenanceWorkSourceKind.Defect => new(
                $"/maintenance/defects/{workOrder.SourceId}",
                [MaintenancePermissions.Read(workOrder.ScopeId)]),
            MaintenanceWorkSourceKind.Forecast => new(
                $"/maintenance/forecast/{workOrder.SourceId}",
                [MaintenancePermissions.Read(workOrder.ScopeId)]),
            _ => throw new ArgumentOutOfRangeException(nameof(workOrder)),
        };
}

public static class MaintenanceNucleusContract
{
    public const int Version = 2;
    public const string Lifecycle = "Overdue>Assigned>Accepted>InProgress>PendingAcceptance>Completed";
    public const string Sources = "Request|Defect|Forecast";
}

public sealed record ClaimMaintenanceWorkOrder(
    MaintenanceWorkOrderId WorkOrderId,
    StateVersion ExpectedVersion,
    string IdempotencyKey);

public sealed record TransitionMaintenanceWorkOrder(
    MaintenanceWorkOrderId WorkOrderId,
    StateVersion ExpectedVersion,
    string IdempotencyKey,
    bool SafetyAcknowledged = false);

public sealed record CompleteWorkOrderChecklistItem(
    MaintenanceWorkOrderId WorkOrderId,
    WorkOrderChecklistItemId ItemId,
    StateVersion ExpectedVersion,
    string IdempotencyKey);

public sealed record UpdateWorkOrderChecklistItem(
    MaintenanceWorkOrderId WorkOrderId,
    WorkOrderChecklistItemId ItemId,
    bool Completed,
    StateVersion ExpectedVersion,
    string IdempotencyKey);

public sealed record MaintenanceWorkUserContext(SessionSnapshot Session, PersonId PersonId);

public enum MaintenanceWorkCommandDisposition { Applied = 1, Replay = 2 }
public sealed record MaintenanceWorkCommandResult<T>(T Value, MaintenanceWorkCommandDisposition Disposition);

public static class MaintenanceWorkPermissions
{
    public static PermissionCode Manage(FacilityScopeId scopeId) =>
        PermissionCode.From($"maintenance.scope.s{scopeId.Value:N}.work.manage");

    public static PermissionCode Execute(FacilityScopeId scopeId) =>
        PermissionCode.From($"maintenance.scope.s{scopeId.Value:N}.work.execute");

    public static PermissionCode Accept(FacilityScopeId scopeId) =>
        PermissionCode.From($"maintenance.scope.s{scopeId.Value:N}.work.accept");
}
