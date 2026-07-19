using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Semantics;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class FacilityEquipmentIdentityTests
{
    [Fact]
    public void FacilityEquipmentAndPointIdentitiesRemainDistinct()
    {
        var value = Guid.Parse("61000000-0000-0000-0000-000000000001");

        Assert.Equal(value, FacilityScopeId.From(value).Value);
        Assert.Equal(value, LocationId.From(value).Value);
        Assert.Equal(value, EquipmentId.From(value).Value);
        Assert.Equal(value, PointId.From(value).Value);
        Assert.NotEqual(typeof(LocationId), typeof(EquipmentId));
        Assert.NotEqual(typeof(EquipmentId), typeof(PointId));
    }

    [Fact]
    public void ScopePermissionsAreExactAndContainNoWildcard()
    {
        var scope = FacilityScopeId.From(Guid.Parse("61000000-0000-0000-0000-000000000002"));

        Assert.Equal(
            "facility.scope.s61000000000000000000000000000002.write",
            FacilityPermissions.Write(scope).Value);
        Assert.Equal(
            "equipment.scope.s61000000000000000000000000000002.read",
            EquipmentPermissions.Read(scope).Value);
        Assert.DoesNotContain('*', FacilityPermissions.Write(scope).Value);
    }
}
