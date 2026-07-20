using Dispatcher.Core;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Dashboards;

public readonly record struct DashboardId
{
    private readonly CanonicalId<DashboardId> value;
    private DashboardId(CanonicalId<DashboardId> value) => this.value = value;
    public Guid Value => value.Value;
    public static DashboardId New() => new(CanonicalId.New<DashboardId>());
    public static DashboardId From(Guid value) => new(CanonicalId.From<DashboardId>(value));
}

public readonly record struct DashboardRevisionId
{
    private readonly CanonicalId<DashboardRevisionId> value;
    private DashboardRevisionId(CanonicalId<DashboardRevisionId> value) => this.value = value;
    public Guid Value => value.Value;
    public static DashboardRevisionId New() => new(CanonicalId.New<DashboardRevisionId>());
    public static DashboardRevisionId From(Guid value) => new(CanonicalId.From<DashboardRevisionId>(value));
}

public readonly record struct DashboardWindowId
{
    private readonly CanonicalId<DashboardWindowId> value;
    private DashboardWindowId(CanonicalId<DashboardWindowId> value) => this.value = value;
    public Guid Value => value.Value;
    public static DashboardWindowId New() => new(CanonicalId.New<DashboardWindowId>());
    public static DashboardWindowId From(Guid value) => new(CanonicalId.From<DashboardWindowId>(value));
}

public readonly record struct WidgetId
{
    private readonly CanonicalId<WidgetId> value;
    private WidgetId(CanonicalId<WidgetId> value) => this.value = value;
    public Guid Value => value.Value;
    public static WidgetId New() => new(CanonicalId.New<WidgetId>());
    public static WidgetId From(Guid value) => new(CanonicalId.From<WidgetId>(value));
}

public readonly record struct DashboardBindingId
{
    private readonly CanonicalId<DashboardBindingId> value;
    private DashboardBindingId(CanonicalId<DashboardBindingId> value) => this.value = value;
    public Guid Value => value.Value;
    public static DashboardBindingId New() => new(CanonicalId.New<DashboardBindingId>());
    public static DashboardBindingId From(Guid value) => new(CanonicalId.From<DashboardBindingId>(value));
}

public enum DashboardBindingSource
{
    Current = 1,
    Alarm = 2,
    History = 3,
}

public sealed record DashboardBinding(
    DashboardBindingId BindingId,
    DashboardBindingSource Source,
    RuntimeScopeId ScopeId,
    PointId PointId,
    PermissionCode RequiredPermission);

public sealed record Widget(
    WidgetId WidgetId,
    string Kind,
    string Title,
    IReadOnlyList<DashboardBindingId> BindingIds);

public sealed record DashboardWindow(
    DashboardWindowId WindowId,
    string Title,
    IReadOnlyList<Widget> Widgets,
    IReadOnlyList<DashboardBinding> Bindings);

public sealed record DashboardDependency(
    DashboardBindingId BindingId,
    string Key,
    string Fingerprint);

public sealed record DashboardRevision(
    DashboardId DashboardId,
    DashboardRevisionId RevisionId,
    ulong RevisionNumber,
    string Name,
    string? Description,
    IReadOnlyList<DashboardWindow> Windows,
    IReadOnlyList<DashboardDependency> Dependencies,
    DateTimeOffset PublishedAt);

public sealed record PublishedDashboardManifest(
    DashboardRevision Revision,
    string ManifestJson,
    string ManifestFingerprint,
    string DependencyFingerprint);

public sealed record DashboardCatalogItem(
    DashboardId DashboardId,
    string Name,
    string? Description,
    bool IsFavorite,
    DateTimeOffset? LastOpenedAt);

public static class DashboardPermissions
{
    public static PermissionCode CatalogRead { get; } = PermissionCode.From("dashboards.catalog.read");
    public static PermissionCode Personalize { get; } = PermissionCode.From("dashboards.personal.write");
    public static PermissionCode Read(DashboardId dashboardId) =>
        PermissionCode.From($"dashboards.d{dashboardId.Value:N}.read");
}
