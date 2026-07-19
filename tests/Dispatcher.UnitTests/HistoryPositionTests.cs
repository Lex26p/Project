using Dispatcher.History;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class HistoryPositionTests
{
    [Fact]
    public void RuntimeFactAndHistoryStreamPositionsRemainDistinctAndMonotonic()
    {
        var runtime = new RuntimeFactPosition(7);
        var history = new HistoryStreamPosition(7);

        Assert.NotEqual(typeof(RuntimeFactPosition), typeof(HistoryStreamPosition));
        Assert.Equal((ulong)8, runtime.Next().Value);
        Assert.Equal((ulong)8, history.Next().Value);
    }
}
