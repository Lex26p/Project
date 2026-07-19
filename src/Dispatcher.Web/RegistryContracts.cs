namespace Dispatcher.Web;

public sealed record RegistryScopePayload(Guid ScopeId, string Label, int LocationCount, int EquipmentCount);

public sealed record LocationSummaryPayload(
    Guid LocationId,
    Guid? PhysicalParentId,
    string Code,
    string Name,
    int ChildCount,
    int EquipmentCount);

public sealed record LocationListPayload(Guid ScopeId, int Total, IReadOnlyList<LocationSummaryPayload> Items);

public sealed record LocationRelationPayload(Guid LocationId, string Code, string Name);

public sealed record EquipmentSummaryPayload(
    Guid EquipmentId,
    Guid LocationId,
    string Code,
    string Name,
    int PointCount);

public sealed record LocationDetailPayload(
    Guid ScopeId,
    LocationSummaryPayload Location,
    IReadOnlyList<LocationSummaryPayload> Children,
    IReadOnlyList<LocationRelationPayload> FunctionalIncoming,
    IReadOnlyList<LocationRelationPayload> FunctionalOutgoing,
    IReadOnlyList<EquipmentSummaryPayload> Equipment);

public sealed record LocationPlanContextPayload(
    Guid ScopeId,
    LocationSummaryPayload Location,
    IReadOnlyList<LocationRelationPayload> Ancestors,
    IReadOnlyList<LocationSummaryPayload> Children,
    IReadOnlyList<EquipmentSummaryPayload> Equipment);

public sealed record EquipmentListPayload(Guid ScopeId, int Total, IReadOnlyList<EquipmentSummaryPayload> Items);

public sealed record EquipmentCurrentPayload(
    long Value,
    string Unit,
    string Quality,
    string Freshness,
    DateTimeOffset SourceTimestamp,
    DateTimeOffset ReceiveTimestamp,
    DateTimeOffset ProcessedTimestamp,
    bool IsUsable);

public sealed record EquipmentPointPayload(
    Guid PointId,
    string Code,
    string Name,
    string Unit,
    EquipmentCurrentPayload? Current);

public sealed record EquipmentDetailPayload(
    Guid ScopeId,
    EquipmentSummaryPayload Equipment,
    LocationRelationPayload Location,
    string CurrentStatus,
    string ConnectionStatus,
    string ConnectionStatusReason,
    IReadOnlyList<EquipmentPointPayload> Points);
