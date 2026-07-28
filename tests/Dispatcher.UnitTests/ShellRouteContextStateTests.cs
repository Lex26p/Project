using Dispatcher.Web;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class ShellRouteContextStateTests
{
    [Theory]
    [InlineData("", "Home")]
    [InlineData("home", "Home")]
    [InlineData("history?point=1", "History")]
    [InlineData(
        "administration/identity",
        "Administration · Identity")]
    [InlineData("unknown-area", "Unknown Area")]
    public void
        ParseMapsRouteToStableSection(
            string route,
            string expectedSection)
    {
        var context =
            ShellRouteContextState.Parse(route);

        Assert.Equal(
            expectedSection,
            context.Section);
        Assert.False(context.HasEntity);
    }

    [Theory]
    [InlineData(
        "locations/01234567-89ab-cdef-0123-456789abcdef",
        "Locations",
        "Location")]
    [InlineData(
        "equipment/01234567-89ab-cdef-0123-456789abcdef",
        "Equipment",
        "Equipment")]
    [InlineData(
        "dashboards/01234567-89ab-cdef-0123-456789abcdef",
        "Dashboards",
        "Dashboard")]
    public void ParseKeepsEntityContextClientOnly(
        string route,
        string expectedSection,
        string expectedKind)
    {
        var context =
            ShellRouteContextState.Parse(route);

        Assert.Equal(
            expectedSection,
            context.Section);
        Assert.Equal(
            expectedKind,
            context.EntityKind);
        Assert.Equal<Guid?>(
            Guid.Parse(
                "01234567-89ab-cdef-0123-456789abcdef"),
            context.EntityId);
        Assert.True(context.HasEntity);
    }
}
