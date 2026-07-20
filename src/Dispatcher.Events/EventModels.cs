using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Semantics;

namespace Dispatcher.Events;

public readonly record struct OperationalEventId
{
    private readonly CanonicalId<OperationalEventId> value;

    private OperationalEventId(CanonicalId<OperationalEventId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static OperationalEventId New() => new(CanonicalId.New<OperationalEventId>());

    public static OperationalEventId From(Guid value) => new(CanonicalId.From<OperationalEventId>(value));
}

public readonly record struct EventJournalPosition
{
    public EventJournalPosition(ulong value)
    {
        ArgumentOutOfRangeException.ThrowIfZero(value);
        Value = value;
    }

    public ulong Value { get; }
}

public readonly record struct OccurrenceProjectionVersion
{
    public OccurrenceProjectionVersion(ulong value)
    {
        ArgumentOutOfRangeException.ThrowIfZero(value);
        Value = value;
    }

    public ulong Value { get; }
}

public enum OperationalEventKind
{
    AlarmRaised = 1,
    AlarmPendingClear = 2,
    AlarmCleared = 3,
}

public sealed record OperationalEventRecord(
    OperationalEventId EventId,
    EventJournalPosition Position,
    RuntimeScopeId ScopeId,
    PointId PointId,
    AlarmOccurrenceId OccurrenceId,
    StateVersion SourceConditionVersion,
    AlarmPriority Priority,
    OperationalEventKind Kind,
    DateTimeOffset OccurredAt,
    DateTimeOffset AcceptedAt);

public sealed record OccurrenceProjectionRecord(
    OccurrenceProjectionVersion Version,
    AlarmOccurrenceSnapshot Occurrence);

public sealed record EventAcceptanceResult(
    OperationalEventRecord Event,
    OccurrenceProjectionRecord Projection,
    bool EventInserted,
    bool ProjectionChanged);

public sealed record EventDispatcherLimits
{
    public EventDispatcherLimits(int maxPageSize, int retainedProjectionChanges, int maxFeedChanges)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedProjectionChanges);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFeedChanges);
        if (maxFeedChanges > retainedProjectionChanges)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFeedChanges),
                "Feed batch size cannot exceed retained projection changes.");
        }

        MaxPageSize = maxPageSize;
        RetainedProjectionChanges = retainedProjectionChanges;
        MaxFeedChanges = maxFeedChanges;
    }

    public int MaxPageSize { get; }

    public int RetainedProjectionChanges { get; }

    public int MaxFeedChanges { get; }
}

public sealed record EventQueryCursor(EventJournalPosition After, EventJournalPosition UpperBound);

public sealed record EventQueryRequest(
    RuntimeScopeId ScopeId,
    IReadOnlySet<PointId> PointIds,
    IReadOnlySet<OperationalEventKind> Kinds,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int PageSize,
    EventQueryCursor? Cursor = null,
    IReadOnlySet<AlarmPriority>? Priorities = null);

public sealed record EventQueryPage(
    IReadOnlyList<OperationalEventRecord> Events,
    EventJournalPosition? UpperBound,
    EventQueryCursor? NextCursor);

public sealed record EventDispatcherCounters(
    long EventCount,
    long ActiveOccurrenceCount,
    long UnacknowledgedOccurrenceCount);

public enum OccurrenceFeedKind
{
    NoChange = 1,
    Delta = 2,
    Gap = 3,
}

public sealed record OccurrenceFeedPage(
    OccurrenceFeedKind Kind,
    ulong From,
    ulong To,
    IReadOnlyList<OccurrenceProjectionRecord> Changes);

public sealed record OccurrenceProjectionSnapshot(
    ulong Cursor,
    IReadOnlyList<OccurrenceProjectionRecord> Occurrences);
