namespace Dispatcher.Web;

public sealed record DashboardSubscriptionRequest(IReadOnlyList<Guid> VisibleWindowIds);
public sealed record DashboardSubscriptionPayload(
    Guid SubscriptionId,
    Guid DashboardId,
    Guid RevisionId,
    IReadOnlyList<DashboardSubscriptionWindowPayload> Windows,
    IReadOnlyList<DashboardSubscriptionLinkPayload> Links);
public sealed record DashboardSubscriptionWindowPayload(
    Guid WindowId,
    IReadOnlyList<DashboardSubscriptionWidgetPayload> Widgets);
public sealed record DashboardSubscriptionWidgetPayload(Guid WidgetId, IReadOnlyList<Guid> BindingIds);
public sealed record DashboardSubscriptionLinkPayload(
    Guid BindingId, string Source, Guid ScopeId, Guid PointId, string Endpoint);
public sealed record DashboardSubscriptionStatusPayload(bool IsCurrent);

public enum DashboardBindingAvailability
{
    Missing = 1,
    Ready = 2,
    Stale = 3,
}

public enum DashboardWidgetAvailability
{
    Ready = 1,
    Partial = 2,
    Stale = 3,
}

public sealed record DashboardBindingUpdate(
    Guid BindingId,
    DashboardBindingAvailability Availability,
    RuntimePointPayload? Current = null);

public sealed record DashboardProtectedTransition(
    Guid BindingId,
    ulong Position,
    string Kind);

public sealed record DashboardWidgetRuntimeState(
    Guid WidgetId,
    DashboardWidgetAvailability Availability);
