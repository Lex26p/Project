using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Equipment;

public readonly record struct EquipmentId
{
    private readonly CanonicalId<EquipmentId> value;

    private EquipmentId(CanonicalId<EquipmentId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static EquipmentId From(Guid value) => new(CanonicalId.From<EquipmentId>(value));
}

public sealed record EquipmentSnapshot(
    EquipmentId EquipmentId,
    FacilityScopeId ScopeId,
    LocationId LocationId,
    string Code,
    string Name,
    long Version);

public sealed record PointDefinition(
    PointId PointId,
    EquipmentId EquipmentId,
    string Code,
    string Name,
    Unit Unit,
    long Version);

public sealed record EquipmentRegistry(
    FacilityScopeId ScopeId,
    IReadOnlyList<EquipmentSnapshot> Equipment,
    IReadOnlyList<PointDefinition> Points);

public sealed record CreateEquipment(
    EquipmentId EquipmentId,
    FacilityScopeId ScopeId,
    LocationId LocationId,
    string Code,
    string Name);

public sealed record MoveEquipment(
    EquipmentId EquipmentId,
    LocationId LocationId,
    long ExpectedVersion);

public sealed record AddPointDefinition(
    EquipmentId EquipmentId,
    PointId PointId,
    string Code,
    string Name,
    Unit Unit,
    long ExpectedEquipmentVersion);

public sealed record EquipmentMutation(EquipmentId EquipmentId, long Version);

public static class EquipmentPermissions
{
    public static PermissionCode Read(FacilityScopeId scopeId) =>
        PermissionCode.From($"equipment.scope.s{scopeId.Value:N}.read");

    public static PermissionCode Write(FacilityScopeId scopeId) =>
        PermissionCode.From($"equipment.scope.s{scopeId.Value:N}.write");
}
