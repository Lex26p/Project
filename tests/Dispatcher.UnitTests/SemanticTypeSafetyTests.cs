using System.Reflection;
using Dispatcher.Semantics;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class SemanticTypeSafetyTests
{
    [Fact]
    public void CanonicalIdentitiesPreserveTheirScope()
    {
        var raw = Guid.Parse("018f0f5d-8a62-7b50-9f41-4d550c6c57c4");
        var deviceId = CanonicalId.From<DeviceScope>(raw);
        var facilityId = CanonicalId.From<FacilityScope>(raw);

        Assert.Equal(raw, deviceId.Value);
        Assert.Equal(raw.ToString("D"), deviceId.ToString());
        Assert.NotEqual(deviceId.GetType(), facilityId.GetType());
        Assert.Empty(ImplicitConversions(typeof(CanonicalId<DeviceScope>)));
    }

    [Fact]
    public void EmptyAndNonCanonicalIdentitiesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => CanonicalId.From<DeviceScope>(Guid.Empty));
        Assert.False(CanonicalId.TryParse<DeviceScope>("not-an-id", out _));
        Assert.Throws<FormatException>(() => CanonicalId.Parse<DeviceScope>("not-an-id"));
    }

    [Fact]
    public void RevisionAndVersionAreDistinctWithoutImplicitConversions()
    {
        Assert.NotEqual(typeof(RevisionNumber), typeof(StateVersion));
        Assert.Empty(ImplicitConversions(typeof(RevisionNumber)));
        Assert.Empty(ImplicitConversions(typeof(StateVersion)));
    }

    private static IEnumerable<MethodInfo> ImplicitConversions(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "op_Implicit");

    private sealed class DeviceScope;

    private sealed class FacilityScope;
}
