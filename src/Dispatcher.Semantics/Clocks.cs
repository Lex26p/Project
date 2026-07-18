namespace Dispatcher.Semantics;

public interface IWallClock
{
    DateTimeOffset GetUtcNow();
}

public interface IMonotonicClock
{
    MonotonicTimestamp GetTimestamp();

    TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish);
}

public readonly record struct MonotonicTimestamp(long Value) : IComparable<MonotonicTimestamp>
{
    public int CompareTo(MonotonicTimestamp other) => Value.CompareTo(other.Value);

    public static bool operator <(MonotonicTimestamp left, MonotonicTimestamp right) => left.CompareTo(right) < 0;

    public static bool operator <=(MonotonicTimestamp left, MonotonicTimestamp right) => left.CompareTo(right) <= 0;

    public static bool operator >(MonotonicTimestamp left, MonotonicTimestamp right) => left.CompareTo(right) > 0;

    public static bool operator >=(MonotonicTimestamp left, MonotonicTimestamp right) => left.CompareTo(right) >= 0;
}

public sealed class SystemClock : IWallClock, IMonotonicClock
{
    private readonly TimeProvider timeProvider;

    public SystemClock(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.timeProvider = timeProvider;
    }

    public static SystemClock Instance { get; } = new(TimeProvider.System);

    public DateTimeOffset GetUtcNow() => timeProvider.GetUtcNow();

    public MonotonicTimestamp GetTimestamp() => new(timeProvider.GetTimestamp());

    public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish)
    {
        if (finish < start)
        {
            throw new ArgumentOutOfRangeException(nameof(finish), "Monotonic time cannot move backwards.");
        }

        return timeProvider.GetElapsedTime(start.Value, finish.Value);
    }
}
