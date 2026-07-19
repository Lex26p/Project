using Dispatcher.Core;
using Dispatcher.Semantics;

namespace Dispatcher.History;

public readonly record struct RuntimeFactPosition(ulong Value) : IComparable<RuntimeFactPosition>
{
    public RuntimeFactPosition Next() => new(checked(Value + 1));

    public int CompareTo(RuntimeFactPosition other) => Value.CompareTo(other.Value);

    public static bool operator <(RuntimeFactPosition left, RuntimeFactPosition right) => left.CompareTo(right) < 0;

    public static bool operator <=(RuntimeFactPosition left, RuntimeFactPosition right) => left.CompareTo(right) <= 0;

    public static bool operator >(RuntimeFactPosition left, RuntimeFactPosition right) => left.CompareTo(right) > 0;

    public static bool operator >=(RuntimeFactPosition left, RuntimeFactPosition right) => left.CompareTo(right) >= 0;
}

public readonly record struct HistoryStreamPosition(ulong Value) : IComparable<HistoryStreamPosition>
{
    public HistoryStreamPosition Next() => new(checked(Value + 1));

    public int CompareTo(HistoryStreamPosition other) => Value.CompareTo(other.Value);

    public static bool operator <(HistoryStreamPosition left, HistoryStreamPosition right) => left.CompareTo(right) < 0;

    public static bool operator <=(HistoryStreamPosition left, HistoryStreamPosition right) => left.CompareTo(right) <= 0;

    public static bool operator >(HistoryStreamPosition left, HistoryStreamPosition right) => left.CompareTo(right) > 0;

    public static bool operator >=(HistoryStreamPosition left, HistoryStreamPosition right) => left.CompareTo(right) >= 0;
}

public enum HistoryIngestStatus
{
    Accepted = 1,
    Duplicate = 2,
}

public sealed record HistorySample(
    RuntimeScopeId ScopeId,
    HistoryStreamPosition HistoryStreamPosition,
    RuntimeFactPosition RuntimeFactPosition,
    SourceId SourceId,
    PointId PointId,
    OwnerPosition<SourceObservation> SourcePosition,
    TypedValue<long> Value,
    Unit Unit,
    DataQuality Quality,
    Freshness Freshness,
    SourceTimestamp SourceTimestamp,
    DateTimeOffset AcceptedAt,
    bool IsLate,
    bool IsOutOfOrder);

public sealed record HistoryGap(
    RuntimeScopeId ScopeId,
    HistoryStreamPosition HistoryStreamPosition,
    RuntimeFactPosition RuntimeFactPosition,
    SourceId SourceId,
    ulong FirstSourcePosition,
    ulong LastSourcePosition,
    string Reason,
    DateTimeOffset AcceptedAt);

public sealed record HistoryRecoveryCheckpoint(
    RuntimeScopeId ScopeId,
    RuntimeFactPosition RuntimeFactPosition,
    HistoryStreamPosition HistoryStreamPosition);

public sealed record HistoryIngestAcceptance(
    HistoryIngestStatus Status,
    RuntimeFactPosition RuntimeFactPosition,
    IReadOnlyList<HistorySample> Samples,
    HistoryGap? Gap,
    HistoryRecoveryCheckpoint Checkpoint);

public sealed record HistoryQueryLimits
{
    public HistoryQueryLimits(int maxPageSize, int maxAggregateBuckets)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAggregateBuckets);
        MaxPageSize = maxPageSize;
        MaxAggregateBuckets = maxAggregateBuckets;
    }

    public int MaxPageSize { get; }

    public int MaxAggregateBuckets { get; }
}

public sealed record HistoryQueryCursor(
    HistoryStreamPosition After,
    HistoryStreamPosition UpperBound);

public sealed record HistoryRangeRequest(
    RuntimeScopeId ScopeId,
    SourceId SourceId,
    PointId PointId,
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    int PageSize,
    HistoryQueryCursor? Cursor = null);

public sealed record HistoryRangeRecord(
    HistoryStreamPosition Position,
    HistorySample? Sample,
    HistoryGap? Gap);

public sealed record HistoryRangePage(
    IReadOnlyList<HistoryRangeRecord> Records,
    HistoryStreamPosition UpperBound,
    HistoryQueryCursor? NextCursor);

public sealed record HistoryResolutionPolicy(
    int Version,
    TimeSpan Resolution);

public sealed record HistoryAggregateBucket(
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    long Count,
    double Average,
    long Minimum,
    long Maximum,
    DataQuality Quality,
    Freshness Freshness,
    bool HasGap);

public sealed record HistoryAggregateSeries(
    HistoryResolutionPolicy Policy,
    IReadOnlyList<HistoryAggregateBucket> Buckets);

public sealed record HistoryRetentionPolicy(
    int Version,
    DateTimeOffset DeleteBefore,
    HistoryStreamPosition Through);

public sealed record HistoryRetentionResult(
    int DeletedSamples,
    int DeletedGaps);

public sealed class HistoryIngestCommitHook
{
    private readonly Func<HistoryIngestAcceptance, CancellationToken, Task> callback;

    public HistoryIngestCommitHook(Func<HistoryIngestAcceptance, CancellationToken, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        this.callback = callback;
    }

    public Task AfterCommittedAsync(
        HistoryIngestAcceptance acceptance,
        CancellationToken cancellationToken) =>
        callback(acceptance, cancellationToken);
}
