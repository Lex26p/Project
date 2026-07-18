using Dispatcher.Semantics;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class VersionAndPositionTests
{
    [Fact]
    public void RevisionsAndVersionsAdvanceMonotonically()
    {
        var revision = RevisionNumber.Initial;
        var version = StateVersion.Initial;

        for (ulong expected = 2; expected <= 1_000; expected++)
        {
            revision = revision.Next();
            version = version.Next();

            Assert.Equal(expected, revision.Value);
            Assert.Equal(expected, version.Value);
        }
    }

    [Fact]
    public void UndefinedRevisionsAndVersionsCannotBeCreatedExplicitly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RevisionNumber.From(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => StateVersion.From(0));
    }

    [Fact]
    public void OwnerPositionsAndConsumerCursorsCannotBeMixed()
    {
        var runtimePosition = new OwnerPosition<RuntimeStream>(42);
        var historyPosition = new OwnerPosition<HistoryStream>(42);
        var runtimeCursor = new ConsumerCursor<RuntimeStream>(42);

        Assert.NotEqual(runtimePosition.GetType(), historyPosition.GetType());
        Assert.NotEqual(runtimePosition.GetType(), runtimeCursor.GetType());
        Assert.Equal((ulong)43, runtimePosition.Next().Value);
        Assert.Equal((ulong)43, runtimeCursor.Next().Value);
    }

    [Fact]
    public void PositionsAdvanceMonotonicallyForAllSampledValues()
    {
        for (ulong value = 0; value < 1_000; value++)
        {
            var current = new OwnerPosition<RuntimeStream>(value);
            Assert.True(current.Next().CompareTo(current) > 0);
        }
    }

    private sealed class RuntimeStream;

    private sealed class HistoryStream;
}
