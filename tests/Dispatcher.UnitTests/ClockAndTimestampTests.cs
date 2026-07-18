using Dispatcher.Semantics;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class ClockAndTimestampTests
{
    [Fact]
    public void InjectedClocksAreDeterministic()
    {
        var expectedUtc = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(expectedUtc, new MonotonicTimestamp(100), TimeSpan.FromMilliseconds(25));

        Assert.Equal(expectedUtc, clock.GetUtcNow());
        Assert.Equal(new MonotonicTimestamp(100), clock.GetTimestamp());
        Assert.Equal(
            TimeSpan.FromMilliseconds(25),
            clock.GetElapsedTime(new MonotonicTimestamp(100), new MonotonicTimestamp(200)));
    }

    [Fact]
    public void SourceReceiveAndProcessedTimesRemainDistinct()
    {
        var utc = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
        var source = SourceTimestamp.FromUtc(utc);
        var received = ReceiveTimestamp.FromUtc(utc);
        var processed = ProcessedTimestamp.FromUtc(utc);

        Assert.Equal(utc, source.Value);
        Assert.Equal(utc, received.Value);
        Assert.Equal(utc, processed.Value);
        Assert.NotEqual(source.GetType(), received.GetType());
        Assert.NotEqual(received.GetType(), processed.GetType());
    }

    [Fact]
    public void SemanticTimestampsRequireUtc()
    {
        var nonUtc = new DateTimeOffset(2026, 7, 19, 3, 0, 0, TimeSpan.FromHours(3));

        Assert.Throws<ArgumentException>(() => SourceTimestamp.FromUtc(nonUtc));
        Assert.Throws<ArgumentException>(() => ReceiveTimestamp.FromUtc(nonUtc));
        Assert.Throws<ArgumentException>(() => ProcessedTimestamp.FromUtc(nonUtc));
    }

    private sealed class FakeClock : IWallClock, IMonotonicClock
    {
        private readonly DateTimeOffset utcNow;
        private readonly MonotonicTimestamp timestamp;
        private readonly TimeSpan elapsed;

        public FakeClock(DateTimeOffset utcNow, MonotonicTimestamp timestamp, TimeSpan elapsed)
        {
            this.utcNow = utcNow;
            this.timestamp = timestamp;
            this.elapsed = elapsed;
        }

        public DateTimeOffset GetUtcNow() => utcNow;

        public MonotonicTimestamp GetTimestamp() => timestamp;

        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) => elapsed;
    }
}
