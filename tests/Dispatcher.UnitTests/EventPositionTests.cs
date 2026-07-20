using Dispatcher.Events;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class EventPositionTests
{
    [Fact]
    public void JournalPositionAndOccurrenceProjectionVersionRemainDistinct()
    {
        Assert.NotEqual(typeof(EventJournalPosition), typeof(OccurrenceProjectionVersion));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventJournalPosition(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OccurrenceProjectionVersion(0));
    }
}
