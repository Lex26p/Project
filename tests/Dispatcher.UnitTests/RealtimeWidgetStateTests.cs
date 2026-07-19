using Dispatcher.Web;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class RealtimeWidgetStateTests
{
    private static readonly Guid ScopeId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid PointId = Guid.Parse("20000000-0000-0000-0000-000000000001");

    [Fact]
    public void NetworkUpdatesAreCoalescedIntoOneRenderRequest()
    {
        var state = new RealtimeWidgetState();
        state.ApplySnapshot(Snapshot(10));
        Assert.True(state.ConsumeRenderRequest());

        state.ApplyPoll(Delta(0, 1, 11));
        state.ApplyPoll(Delta(1, 2, 12));

        Assert.Equal(12, Assert.Single(state.Points).Value);
        Assert.True(state.ConsumeRenderRequest());
        Assert.False(state.ConsumeRenderRequest());
    }

    [Fact]
    public void GapDisconnectAndPermissionInvalidationRequireResync()
    {
        var state = new RealtimeWidgetState();
        state.ApplySnapshot(Snapshot(10));
        state.ConsumeRenderRequest();

        state.ApplyPoll(Delta(4, 5, 11));
        Assert.True(state.NeedsResync);

        state.ApplySnapshot(Snapshot(12));
        state.MarkDisconnected();
        Assert.True(state.NeedsResync);

        state.ApplyPoll(new RealtimePollPayload(RealtimePollKind.PermissionInvalidated));
        Assert.True(state.PermissionInvalidated);
        Assert.Empty(state.Points);
        Assert.True(state.HasPendingRender);
    }

    private static RuntimeSnapshotPayload Snapshot(long value) =>
        new(ScopeId, 0, [Point(value)]);

    private static RealtimePollPayload Delta(ulong from, ulong to, long value) =>
        new(
            RealtimePollKind.Delta,
            new RuntimeDeltaPayload(ScopeId, from, to, [Point(value)]));

    private static RuntimePointPayload Point(long value) =>
        new(PointId, value, "°C", "Good", "Fresh");
}
