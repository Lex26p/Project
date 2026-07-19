using Dispatcher.Web;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class HistoryTrendStateTests
{
    [Fact]
    public void RealtimeGapClearsLiveTailAndRequiresHistoryRequery()
    {
        var pointId = Guid.Parse("c1000000-0000-7000-8000-000000000001");
        var state = new HistoryTrendState();
        state.ApplyHistory(new HistoryAggregatePayload(1, 60, []));
        state.ApplyLiveSnapshot(
            new RuntimeSnapshotPayload(
                Guid.NewGuid(),
                5,
                [new RuntimePointPayload(pointId, 42, "kW", "Good", "Fresh")]),
            pointId);

        state.MarkRealtimeGap();

        Assert.True(state.RequiresHistoryRequery);
        Assert.Null(state.LivePoint);
    }
}
