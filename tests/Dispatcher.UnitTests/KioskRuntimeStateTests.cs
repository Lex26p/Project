using Dispatcher.Web;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class KioskRuntimeStateTests
{
    [Fact]
    public void OfflinePolicyRetainsOnlyApprovedReadOnlyContentAndNeverQueuesCommands()
    {
        var state = new KioskRuntimeState();
        state.Synchronized(Runtime("ReadOnlyLastSynchronized"));
        state.Disconnected();
        Assert.NotNull(state.Runtime);
        Assert.False(state.Online);
        Assert.False(state.TryQueueCommand());
        Assert.Equal(0, state.PendingCommandCount);

        state.Synchronized(Runtime("Blank"));
        state.Disconnected();
        Assert.Null(state.Runtime);
        Assert.False(state.TryQueueCommand());
        Assert.Equal(0, state.PendingCommandCount);
    }

    private static KioskAssignedRuntimePayload Runtime(string offlineMode) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "Kiosk", offlineMode, false,
        new KioskDashboardPayload(
            Guid.NewGuid(), Guid.NewGuid(), 1, "Assigned", null, [], [], DateTimeOffset.UnixEpoch),
        DateTimeOffset.UnixEpoch);
}
