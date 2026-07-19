namespace Dispatcher.Web;

public sealed class WorkspaceRouteGuardState
{
    private long generation;

    public string Route { get; private set; } = string.Empty;

    public RouteAccess? Access { get; private set; }

    public long BeginNavigation(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        Route = route;
        Access = null;
        return checked(++generation);
    }

    public bool CompleteNavigation(long navigationGeneration, RouteAccess access)
    {
        if (navigationGeneration != generation)
        {
            return false;
        }

        Access = access;
        return true;
    }
}
