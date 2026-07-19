using Dispatcher.Platform;
using Dispatcher.Web;
using Dispatcher.Workspace;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class WorkspaceRouteTests
{
    [Theory]
    [InlineData("/home", "workspace.home.read")]
    [InlineData("/home?from=back", "workspace.home.read")]
    [InlineData("/me", "workspace.profile.self.read")]
    [InlineData("/search?q=pump", "workspace.search.read")]
    [InlineData("/users/20000000-0000-0000-0000-000000000001", "workspace.profile.user.read")]
    public void CanonicalDirectRoutesRequireExactPermission(string route, string permission)
    {
        Assert.Equal(PermissionCode.From(permission), WorkspaceRoutes.RequiredPermission(route));
    }

    [Fact]
    public void UnknownAndMalformedPersonalRoutesAreNotAuthorized()
    {
        Assert.Null(WorkspaceRoutes.RequiredPermission("/dashboard"));
        Assert.Null(WorkspaceRoutes.RequiredPermission("/users/not-a-guid"));
    }

    [Fact]
    public void BackAndForwardKeepOnlyLatestRouteAuthorization()
    {
        var state = new WorkspaceRouteGuardState();
        var home = state.BeginNavigation("/home");
        var me = state.BeginNavigation("/me");

        Assert.False(state.CompleteNavigation(home, RouteAccess.Allowed));
        Assert.True(state.CompleteNavigation(me, RouteAccess.Denied));
        Assert.Equal("/me", state.Route);
        Assert.Equal(RouteAccess.Denied, state.Access);

        var back = state.BeginNavigation("/home");
        Assert.True(state.CompleteNavigation(back, RouteAccess.Allowed));
        Assert.Equal(RouteAccess.Allowed, state.Access);
    }
}
