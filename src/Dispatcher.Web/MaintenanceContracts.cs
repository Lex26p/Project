namespace Dispatcher.Web;

public sealed record MaintenanceOverviewPayload(
    int Overdue,
    int DueToday,
    int RequiresAssignment,
    int InProgress,
    int PendingAcceptance,
    int SafetyAttention);

public sealed record MaintenanceAssetPayload(
    Guid AssetId,
    Guid ScopeId,
    string Code,
    string Name,
    Guid? EquipmentId,
    string? EquipmentHref,
    string LinkState,
    ulong Version,
    bool CanManage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MaintenanceAssetPagePayload(
    IReadOnlyList<MaintenanceAssetPayload> Assets,
    string? NextCode,
    Guid? NextAssetId);

public sealed record MaintenanceForecastPayload(
    Guid ObligationId,
    Guid WorkOrderId,
    Guid PlanId,
    Guid AssetId,
    Guid ScopeId,
    ulong PlanRevision,
    string Title,
    DateOnly DueOn,
    string State,
    bool IsOverdue,
    string? WorkOrderHref,
    bool CanManage);

public sealed record MaintenanceForecastPagePayload(
    IReadOnlyList<MaintenanceForecastPayload> Entries,
    DateOnly? NextDueOn,
    Guid? NextObligationId);

public sealed record MaintenanceRequestPayload(
    Guid RequestId,
    Guid AssetId,
    Guid ScopeId,
    string Summary,
    string State,
    ulong Version,
    string AssetHref,
    string? EventHref,
    bool CanApprove,
    bool CanCreateWorkOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MaintenanceRequestPagePayload(
    IReadOnlyList<MaintenanceRequestPayload> Requests,
    DateTimeOffset? NextCreatedAt,
    Guid? NextRequestId);

public sealed record MaintenanceDefectPayload(
    Guid DefectId,
    Guid AssetId,
    Guid ScopeId,
    string Summary,
    string State,
    ulong Version,
    string AssetHref,
    bool CanConfirm,
    bool CanCreateWorkOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MaintenanceDefectPagePayload(
    IReadOnlyList<MaintenanceDefectPayload> Defects,
    DateTimeOffset? NextCreatedAt,
    Guid? NextDefectId);

public sealed record MaintenanceSafetyPayload(
    bool PermitRequired,
    bool IsolationRequired,
    string? Instructions,
    DateTimeOffset? AcknowledgedAt);

public sealed record MaintenanceChecklistItemPayload(
    Guid ItemId,
    string Description,
    bool Mandatory,
    DateTimeOffset? CompletedAt,
    Guid? CompletedBy);

public sealed record MaintenanceWorkOrderPayload(
    Guid WorkOrderId,
    Guid AssetId,
    Guid ScopeId,
    string SourceKind,
    Guid SourceId,
    string Summary,
    Guid AssignedPersonId,
    string State,
    ulong Version,
    MaintenanceSafetyPayload Safety,
    IReadOnlyList<MaintenanceChecklistItemPayload> Checklist,
    string AssetHref,
    string? SourceHref,
    bool CanClaim,
    bool CanAccept,
    bool CanStart,
    bool CanUpdateChecklist,
    bool CanSubmitForAcceptance,
    bool CanAcceptResult,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MaintenanceWorkOrderPagePayload(
    IReadOnlyList<MaintenanceWorkOrderPayload> WorkOrders,
    DateTimeOffset? NextCreatedAt,
    Guid? NextWorkOrderId);

public sealed record MaintenanceWorkOrderCommandPayload(
    MaintenanceWorkOrderPayload WorkOrder,
    string Disposition);

public sealed record VersionedMaintenanceWorkInput(
    ulong ExpectedVersion,
    string IdempotencyKey,
    bool SafetyAcknowledged = false);

public sealed record UpdateMaintenanceChecklistInput(
    bool Completed,
    ulong ExpectedVersion,
    string IdempotencyKey);

public enum MaintenanceApiStatus
{
    Available = 1,
    SessionExpired = 2,
    Forbidden = 3,
    Invalid = 4,
    NotFound = 5,
    Conflict = 6,
    Unavailable = 7,
}

public sealed record MaintenanceApiResult<TPayload>(MaintenanceApiStatus Status, TPayload? Payload);
