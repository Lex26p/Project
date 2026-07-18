namespace Dispatcher.Semantics;

public readonly record struct SourceTimestamp
{
    private SourceTimestamp(DateTimeOffset value)
    {
        Value = value;
    }

    public DateTimeOffset Value { get; }

    public static SourceTimestamp FromUtc(DateTimeOffset value) => new(TimestampGuard.RequireUtc(value));
}

public readonly record struct ReceiveTimestamp
{
    private ReceiveTimestamp(DateTimeOffset value)
    {
        Value = value;
    }

    public DateTimeOffset Value { get; }

    public static ReceiveTimestamp FromUtc(DateTimeOffset value) => new(TimestampGuard.RequireUtc(value));
}

public readonly record struct ProcessedTimestamp
{
    private ProcessedTimestamp(DateTimeOffset value)
    {
        Value = value;
    }

    public DateTimeOffset Value { get; }

    public static ProcessedTimestamp FromUtc(DateTimeOffset value) => new(TimestampGuard.RequireUtc(value));
}

internal static class TimestampGuard
{
    public static DateTimeOffset RequireUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero
        ? value
        : throw new ArgumentException("A timestamp must use the UTC offset.", nameof(value));
}
