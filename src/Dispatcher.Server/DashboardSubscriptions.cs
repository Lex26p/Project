using System.Collections.Concurrent;
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
    private readonly DashboardSubscriptionGenerationStore generations;

    public DashboardSubscriptionService(
        AuthorizedDashboardService dashboards,
        DashboardRuntimeLimits limits,
        DashboardSubscriptionGenerationStore generations)
    {
        this.dashboards = dashboards;
        this.limits = limits;
        this.generations = generations;
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
        var subscriptionId = generations.Open(
            session!.Id, manifest.Value.DashboardId, manifest.Value.RevisionId);
        return Result.Success(new DashboardSubscriptionPayload(
            subscriptionId,
            manifest.Value.DashboardId.Value,
            manifest.Value.RevisionId.Value,
            windows.Select(window => new DashboardSubscriptionWindowPayload(
                window.WindowId.Value,
                window.Widgets.Select(widget => new DashboardSubscriptionWidgetPayload(
                    widget.WidgetId.Value,
                    widget.BindingIds.Select(id => id.Value).ToArray())).ToArray())).ToArray(),
            links));
    }

    public bool IsCurrent(SessionSnapshot? session, Guid subscriptionId) =>
        session is not null && generations.IsCurrent(subscriptionId, session.Id);

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

public sealed record DashboardSubscriptionGeneration(
    SessionId SessionId,
    DashboardId DashboardId,
    DashboardRevisionId RevisionId);

public sealed class DashboardSubscriptionGenerationStore
{
    private readonly ConcurrentDictionary<Guid, DashboardSubscriptionGeneration> subscriptions = new();

    public Guid Open(SessionId sessionId, DashboardId dashboardId, DashboardRevisionId revisionId)
    {
        var subscriptionId = Guid.CreateVersion7();
        subscriptions[subscriptionId] = new DashboardSubscriptionGeneration(sessionId, dashboardId, revisionId);
        return subscriptionId;
    }

    public bool IsCurrent(Guid subscriptionId, SessionId sessionId) =>
        subscriptions.TryGetValue(subscriptionId, out var subscription) && subscription.SessionId == sessionId;

    public void ClosePreviousGenerations(DashboardId dashboardId, DashboardRevisionId revisionId)
    {
        foreach (var item in subscriptions.Where(item =>
                     item.Value.DashboardId == dashboardId && item.Value.RevisionId != revisionId))
        {
            subscriptions.TryRemove(item.Key, out _);
        }
    }
}

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
