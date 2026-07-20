namespace Dispatcher.Web;

public sealed class DashboardRuntimeState
{
    private readonly int maxBindings;
    private readonly int maxProtectedTransitions;
    private readonly Dictionary<Guid, DashboardBindingAvailability> availability = [];
    private readonly Dictionary<Guid, RuntimePointPayload> current = [];
    private readonly Dictionary<Guid, RuntimePointPayload> pendingCurrent = [];
    private readonly Dictionary<Guid, ulong> alarmPositions = [];
    private readonly List<DashboardProtectedTransition> protectedTransitions = [];
    private DashboardSubscriptionPayload? manifest;
    private bool visible = true;
    private bool pendingRender;

    public DashboardRuntimeState(int maxBindings, int maxProtectedTransitions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBindings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxProtectedTransitions);
        this.maxBindings = maxBindings;
        this.maxProtectedTransitions = maxProtectedTransitions;
    }

    public bool RequiresResync { get; private set; } = true;
    public bool ProtectedGapDetected { get; private set; }
    public bool ShouldPollCurrent => visible && !RequiresResync;
    public bool ShouldPollProtected => !RequiresResync;
    public int PendingCurrentCount => pendingCurrent.Count;
    public IReadOnlyDictionary<Guid, RuntimePointPayload> Current => current;

    public IReadOnlyList<DashboardWidgetRuntimeState> Widgets => manifest is null
        ? []
        : manifest.Windows.SelectMany(window => window.Widgets).Select(widget =>
        {
            var states = widget.BindingIds.Select(id => availability[id]).ToArray();
            var state = states.Any(item => item == DashboardBindingAvailability.Missing)
                ? DashboardWidgetAvailability.Partial
                : states.Any(item => item == DashboardBindingAvailability.Stale)
                    ? DashboardWidgetAvailability.Stale
                    : DashboardWidgetAvailability.Ready;
            return new DashboardWidgetRuntimeState(widget.WidgetId, state);
        }).ToArray();

    public void ApplyManifest(DashboardSubscriptionPayload subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (subscription.Links.Count > maxBindings ||
            subscription.Links.Select(link => link.BindingId).Distinct().Count() != subscription.Links.Count)
        {
            throw new ArgumentException("Dashboard subscription exceeds binding capacity or contains duplicates.", nameof(subscription));
        }

        var bindingIds = subscription.Links.Select(link => link.BindingId).ToHashSet();
        if (subscription.Windows.SelectMany(window => window.Widgets)
            .Any(widget => widget.BindingIds.Count == 0 || widget.BindingIds.Any(id => !bindingIds.Contains(id))))
        {
            throw new ArgumentException("Dashboard widgets must reference subscription bindings.", nameof(subscription));
        }

        manifest = subscription;
        availability.Clear();
        current.Clear();
        pendingCurrent.Clear();
        alarmPositions.Clear();
        protectedTransitions.Clear();
        foreach (var bindingId in bindingIds)
        {
            availability[bindingId] = DashboardBindingAvailability.Missing;
        }

        RequiresResync = false;
        ProtectedGapDetected = false;
        pendingRender = true;
    }

    public void ApplyCurrent(IEnumerable<DashboardBindingUpdate> updates)
    {
        EnsureManifest();
        foreach (var update in updates)
        {
            if (!IsLink(update.BindingId, "Current"))
            {
                continue;
            }

            availability[update.BindingId] = update.Availability;
            if (update.Current is not null)
            {
                current[update.BindingId] = update.Current;
                pendingCurrent[update.BindingId] = update.Current;
            }

            pendingRender = true;
        }
    }

    public void ApplyAlarmSnapshot(IEnumerable<Guid> availableBindingIds)
    {
        EnsureManifest();
        foreach (var bindingId in availableBindingIds.Where(id => IsLink(id, "Alarm")))
        {
            availability[bindingId] = DashboardBindingAvailability.Ready;
            pendingRender = true;
        }
    }

    public bool ApplyProtectedTransitions(IEnumerable<DashboardProtectedTransition> transitions)
    {
        EnsureManifest();
        foreach (var transition in transitions)
        {
            if (!IsLink(transition.BindingId, "Alarm") ||
                alarmPositions.TryGetValue(transition.BindingId, out var position) && position >= transition.Position)
            {
                continue;
            }

            if (protectedTransitions.Count == maxProtectedTransitions)
            {
                ProtectedGapDetected = true;
                RequiresResync = true;
                pendingRender = true;
                return false;
            }

            alarmPositions[transition.BindingId] = transition.Position;
            availability[transition.BindingId] = DashboardBindingAvailability.Ready;
            protectedTransitions.Add(transition);
            pendingRender = true;
        }

        return true;
    }

    public void ApplyHistory(Guid bindingId, bool stale)
    {
        EnsureManifest();
        if (IsLink(bindingId, "History"))
        {
            availability[bindingId] = stale
                ? DashboardBindingAvailability.Stale
                : DashboardBindingAvailability.Ready;
            pendingRender = true;
        }
    }

    public IReadOnlyList<DashboardProtectedTransition> DrainProtectedTransitions()
    {
        var result = protectedTransitions.ToArray();
        protectedTransitions.Clear();
        return result;
    }

    public void SetVisible(bool value)
    {
        visible = value;
        if (visible && (pendingCurrent.Count > 0 || protectedTransitions.Count > 0))
        {
            pendingRender = true;
        }
    }

    public void MarkDisconnected()
    {
        foreach (var bindingId in availability.Keys.ToArray())
        {
            if (availability[bindingId] == DashboardBindingAvailability.Ready)
            {
                availability[bindingId] = DashboardBindingAvailability.Stale;
            }
        }

        RequiresResync = true;
        pendingRender = true;
    }

    public bool ConsumeRenderRequest()
    {
        if (!visible || !pendingRender)
        {
            return false;
        }

        pendingRender = false;
        pendingCurrent.Clear();
        return true;
    }

    private bool IsLink(Guid bindingId, string source) => manifest!.Links.Any(link =>
        link.BindingId == bindingId && string.Equals(link.Source, source, StringComparison.Ordinal));

    private void EnsureManifest()
    {
        if (manifest is null || RequiresResync)
        {
            throw new InvalidOperationException("Dashboard runtime requires an authorized resnapshot.");
        }
    }
}
