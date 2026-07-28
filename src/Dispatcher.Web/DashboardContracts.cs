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
    IReadOnlyList<DashboardSubscriptionWidgetPayload> Widgets,
    IReadOnlyList<Guid>? MimicBindingIds =
        null);
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

public sealed record DashboardCatalogPayload(
    Guid DashboardId,
    string Name,
    string? Description,
    bool IsFavorite,
    DateTimeOffset? LastOpenedAt,
    bool CanEdit = false);

public sealed record DashboardManifestPayload(
    Guid DashboardId,
    Guid RevisionId,
    ulong RevisionNumber,
    string Name,
    string? Description,
    IReadOnlyList<DashboardWindowPayload> Windows,
    IReadOnlyList<DashboardDependencyPayload> Dependencies,
    DateTimeOffset PublishedAt,
    Guid? SelectedWindowId,
    IReadOnlyList<DashboardMimicPayload> Mimics);

public sealed record DashboardWindowPayload(
    Guid WindowId,
    string Title,
    IReadOnlyList<DashboardWidgetPayload> Widgets,
    IReadOnlyList<DashboardBindingPayload> Bindings,
    string Layout = "Widgets",
    Guid? MimicId = null,
    Guid? MimicRevisionId = null);

public sealed record DashboardWidgetPayload(
    Guid WidgetId,
    string Kind,
    string Title,
    IReadOnlyList<Guid> BindingIds);

public sealed record DashboardBindingPayload(
    Guid BindingId,
    string Source,
    Guid ScopeId,
    Guid PointId,
    Guid? HistorySourceId = null);

public sealed record DashboardDependencyPayload(
    Guid BindingId,
    string Key,
    string Fingerprint);

public sealed record DashboardMimicPayload(
    Guid WindowId,
    Guid MimicId,
    Guid RevisionId,
    ulong RevisionNumber,
    string Name,
    string SanitizedSvg,
    IReadOnlyList<DashboardBindingPayload> Bindings,
    bool CanEdit = false);
