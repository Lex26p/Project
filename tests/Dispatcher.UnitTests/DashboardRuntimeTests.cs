using Dispatcher.Web;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class DashboardRuntimeTests
{
    private static readonly Guid CurrentBinding = Guid.Parse("e1000000-0000-7000-8000-000000000001");
    private static readonly Guid AlarmBinding = Guid.Parse("e1000000-0000-7000-8000-000000000002");
    private static readonly Guid HistoryBinding = Guid.Parse("e1000000-0000-7000-8000-000000000003");

    [Fact]
    public void HiddenCurrentCoalescesAndWidgetReportsPartialThenStale()
    {
        var state = new DashboardRuntimeState(maxBindings: 8, maxProtectedTransitions: 8);
        state.ApplyManifest(Manifest());
        Assert.True(state.ConsumeRenderRequest());
        state.SetVisible(false);

        state.ApplyCurrent([
            Update(CurrentBinding, 1, "Fresh"),
            Update(CurrentBinding, 2, "Fresh"),
        ]);

        Assert.False(state.ShouldPollCurrent);
        Assert.True(state.ShouldPollProtected);
        Assert.Equal(1, state.PendingCurrentCount);
        Assert.False(state.ConsumeRenderRequest());
        Assert.Equal(DashboardWidgetAvailability.Partial, Assert.Single(state.Widgets).Availability);

        state.ApplyAlarmSnapshot([AlarmBinding]);
        state.ApplyHistory(HistoryBinding, stale: true);
        state.SetVisible(true);
        Assert.True(state.ConsumeRenderRequest());
        Assert.Equal(2, state.Current[CurrentBinding].Value);
        Assert.Equal(DashboardWidgetAvailability.Stale, Assert.Single(state.Widgets).Availability);
    }

    [Fact]
    public void ProtectedAlarmTransitionsAreNeverSilentlyCoalesced()
    {
        var state = new DashboardRuntimeState(maxBindings: 8, maxProtectedTransitions: 2);
        state.ApplyManifest(Manifest());

        Assert.True(state.ApplyProtectedTransitions([
            new DashboardProtectedTransition(AlarmBinding, 1, "Raised"),
            new DashboardProtectedTransition(AlarmBinding, 2, "Acknowledged"),
        ]));
        var transitions = state.DrainProtectedTransitions();
        Assert.Equal(["Raised", "Acknowledged"], transitions.Select(item => item.Kind));

        Assert.True(state.ApplyProtectedTransitions([
            new DashboardProtectedTransition(AlarmBinding, 3, "Assigned"),
            new DashboardProtectedTransition(AlarmBinding, 4, "Cleared"),
        ]));
        Assert.False(state.ApplyProtectedTransitions([
            new DashboardProtectedTransition(AlarmBinding, 5, "Reopened"),
        ]));
        Assert.True(state.ProtectedGapDetected);
        Assert.True(state.RequiresResync);
    }

    [Fact]
    public void SlowClientStateIsIsolatedAndDisconnectRequiresResnapshot()
    {
        var slow = new DashboardRuntimeState(maxBindings: 8, maxProtectedTransitions: 8);
        var fast = new DashboardRuntimeState(maxBindings: 8, maxProtectedTransitions: 8);
        slow.ApplyManifest(Manifest());
        fast.ApplyManifest(Manifest());
        slow.SetVisible(false);

        slow.ApplyCurrent([Update(CurrentBinding, 10, "Fresh")]);
        fast.ApplyCurrent([Update(CurrentBinding, 20, "Fresh")]);
        Assert.True(fast.ConsumeRenderRequest());
        Assert.Equal(20, fast.Current[CurrentBinding].Value);
        Assert.Equal(10, slow.Current[CurrentBinding].Value);

        fast.MarkDisconnected();
        Assert.True(fast.RequiresResync);
        Assert.False(slow.RequiresResync);
        Assert.Equal(DashboardWidgetAvailability.Partial, Assert.Single(slow.Widgets).Availability);
    }

    private static DashboardBindingUpdate Update(Guid bindingId, long value, string freshness) => new(
        bindingId,
        freshness == "Stale" ? DashboardBindingAvailability.Stale : DashboardBindingAvailability.Ready,
        new RuntimePointPayload(Guid.NewGuid(), value, "u", "Good", freshness));

    private static DashboardSubscriptionPayload Manifest()
    {
        var widgetId = Guid.Parse("e2000000-0000-7000-8000-000000000001");
        var windowId = Guid.Parse("e3000000-0000-7000-8000-000000000001");
        var scopeId = Guid.Parse("e4000000-0000-7000-8000-000000000001");
        return new DashboardSubscriptionPayload(
            Guid.Parse("e5000000-0000-7000-8000-000000000001"),
            Guid.Parse("e6000000-0000-7000-8000-000000000001"),
            [new DashboardSubscriptionWindowPayload(
                windowId,
                [new DashboardSubscriptionWidgetPayload(
                    widgetId, [CurrentBinding, AlarmBinding, HistoryBinding])])],
            [
                new DashboardSubscriptionLinkPayload(CurrentBinding, "Current", scopeId, Guid.NewGuid(), "/hubs/runtime"),
                new DashboardSubscriptionLinkPayload(AlarmBinding, "Alarm", scopeId, Guid.NewGuid(), "/hubs/events"),
                new DashboardSubscriptionLinkPayload(HistoryBinding, "History", scopeId, Guid.NewGuid(), "/api/history/aggregate"),
            ]);
    }
}
