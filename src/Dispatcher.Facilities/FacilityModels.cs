using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Facilities;

public readonly record struct FacilityScopeId
{
    private readonly CanonicalId<FacilityScopeId> value;

    private FacilityScopeId(CanonicalId<FacilityScopeId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static FacilityScopeId From(Guid value) => new(CanonicalId.From<FacilityScopeId>(value));
}

public readonly record struct LocationId
{
    private readonly CanonicalId<LocationId> value;

    private LocationId(CanonicalId<LocationId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static LocationId From(Guid value) => new(CanonicalId.From<LocationId>(value));
}

public sealed record LocationSnapshot(
    LocationId LocationId,
    FacilityScopeId ScopeId,
    LocationId? PhysicalParentId,
    string Code,
    string Name,
    long Version);

public sealed record FunctionalLocationRelation(
    LocationId SourceLocationId,
    LocationId TargetLocationId);

public sealed record FacilityGraph(
    FacilityScopeId ScopeId,
    IReadOnlyList<LocationSnapshot> Locations,
    IReadOnlyList<FunctionalLocationRelation> FunctionalRelations);

public sealed record CreateLocation(
    LocationId LocationId,
    FacilityScopeId ScopeId,
    LocationId? PhysicalParentId,
    string Code,
    string Name);

public sealed record MoveLocation(
    LocationId LocationId,
    LocationId? PhysicalParentId,
    long ExpectedVersion);

public sealed record AddFunctionalLocationRelation(
    LocationId SourceLocationId,
    LocationId TargetLocationId,
    long ExpectedSourceVersion);

public sealed record FacilityMutation(LocationId LocationId, long Version);

public static class FacilityPermissions
{
    public static PermissionCode Read(FacilityScopeId scopeId) =>
        PermissionCode.From($"facility.scope.s{scopeId.Value:N}.read");

    public static PermissionCode Write(FacilityScopeId scopeId) =>
        PermissionCode.From($"facility.scope.s{scopeId.Value:N}.write");
}
