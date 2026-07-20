using Dispatcher.Dashboards;
using Dispatcher.Workspace;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class DashboardContractTests
{
    [Fact]
    public void WindowIsTheScreenContractAndHomeRemainsWorkspace()
    {
        Assert.Equal("/home", WorkspaceRoutes.Home);
        Assert.Null(typeof(DashboardWindow).Assembly.GetType("Dispatcher.Dashboards.Screen"));
        Assert.Null(typeof(DashboardWindow).Assembly.GetType("Dispatcher.Dashboards.DashboardScreen"));
        Assert.NotEqual(typeof(DashboardWindow).Assembly, typeof(WorkspaceHome).Assembly);
    }
}
