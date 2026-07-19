namespace Dispatcher.Web;

public sealed record HistoryRecordPayload(
    ulong Position,
    string Kind,
    long? Value,
    string? Unit,
    string? Quality,
    string? Freshness,
    DateTimeOffset? SourceTimestamp,
    bool? IsLate,
    bool? IsOutOfOrder,
    ulong? GapFirstSourcePosition,
    ulong? GapLastSourcePosition,
    string? GapReason);

public sealed record HistoryRangePayload(
    IReadOnlyList<HistoryRecordPayload> Records,
    ulong UpperBound,
    ulong? NextAfter,
    ulong? NextUpper);

public sealed record HistoryBucketPayload(
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    long Count,
    double Average,
    long Minimum,
    long Maximum,
    string Quality,
    string Freshness,
    bool HasGap);

public sealed record HistoryAggregatePayload(
    int PolicyVersion,
    double ResolutionSeconds,
    IReadOnlyList<HistoryBucketPayload> Buckets);
