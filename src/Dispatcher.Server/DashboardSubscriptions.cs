using Dispatcher.Dashboards;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Server;

public sealed record DashboardRuntimeLimits
{
    public DashboardRuntimeLimits(int maxVisibleWindows, int maxBindings)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxVisibleWindows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBindings);
        MaxVisibleWindows = maxVisibleWindows;
        MaxBindings = maxBindings;
    }

    public int MaxVisibleWindows { get; }
    public int MaxBindings { get; }
}

public sealed class DashboardSubscriptionService
{
    private readonly AuthorizedDashboardService dashboards;
    private readonly DashboardRuntimeLimits limits;

    public DashboardSubscriptionService(
        AuthorizedDashboardService dashboards,
        DashboardRuntimeLimits limits)
    {
        this.dashboards = dashboards;
        this.limits = limits;
    }

    public async Task<Result<DashboardSubscriptionPayload>> CreateAsync(
        SessionSnapshot? session,
        DashboardId dashboardId,
        IReadOnlyCollection<DashboardWindowId> visibleWindowIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(visibleWindowIds);
        var requested = visibleWindowIds.Distinct().ToHashSet();
        if (requested.Count > limits.MaxVisibleWindows)
        {
            return Failure("dashboard.visible_window_capacity", "Visible Dashboard window capacity was exceeded.");
        }

        var manifest = await dashboards.ReadManifestAsync(session, dashboardId, cancellationToken)
            .ConfigureAwait(false);
        if (manifest.IsFailure)
        {
            return Result.Failure<DashboardSubscriptionPayload>(manifest.Error!);
        }

        var knownWindows = manifest.Value.Windows.Select(window => window.WindowId).ToHashSet();
        if (!requested.IsSubsetOf(knownWindows))
        {
            return Failure("dashboard.window_not_found", "A visible Dashboard window was not found.");
        }

        var windows = manifest.Value.Windows.Where(window => requested.Contains(window.WindowId)).ToArray();
        var bindings = windows.SelectMany(window => window.Bindings).ToArray();
        if (bindings.Length > limits.MaxBindings)
        {
            return Failure("dashboard.binding_capacity", "Dashboard subscription binding capacity was exceeded.");
        }

        var links = bindings.Select(ToLink).ToArray();
        return Result.Success(new DashboardSubscriptionPayload(
            manifest.Value.DashboardId.Value,
            manifest.Value.RevisionId.Value,
            windows.Select(window => new DashboardSubscriptionWindowPayload(
                window.WindowId.Value,
                window.Widgets.Select(widget => new DashboardSubscriptionWidgetPayload(
                    widget.WidgetId.Value,
                    widget.BindingIds.Select(id => id.Value).ToArray())).ToArray())).ToArray(),
            links));
    }

    private static DashboardSubscriptionLinkPayload ToLink(DashboardBinding binding) => new(
        binding.BindingId.Value,
        binding.Source.ToString(),
        binding.ScopeId.Value,
        binding.PointId.Value,
        binding.Source switch
        {
            DashboardBindingSource.Current => "/hubs/runtime",
            DashboardBindingSource.Alarm => "/hubs/events",
            DashboardBindingSource.History =>
                $"/api/history/aggregate?scopeId={binding.ScopeId.Value:D}" +
                $"&sourceId={binding.HistorySourceId!.Value.Value:D}&pointId={binding.PointId.Value:D}",
            _ => throw new ArgumentOutOfRangeException(nameof(binding)),
        });

    private static Result<DashboardSubscriptionPayload> Failure(string code, string message) =>
        Result.Failure<DashboardSubscriptionPayload>(new OperationError(ErrorCode.From(code), message));
}

public sealed record DashboardSubscriptionPayload(
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
