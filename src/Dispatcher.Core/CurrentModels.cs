using Dispatcher.Semantics;

namespace Dispatcher.Core;

public sealed record CurrentEntry(
    RuntimeScopeId ScopeId,
    SourceId SourceId,
    PointId PointId,
    OwnerPosition<SourceObservation> SourcePosition,
    OwnerPosition<CurrentEntry> CurrentPosition,
    TypedValue<long> Value,
    Unit Unit,
    DataQuality Quality,
    Freshness Freshness,
    SourceTimestamp SourceTimestamp,
    ReceiveTimestamp ReceiveTimestamp,
    ProcessedTimestamp ProcessedTimestamp,
    MonotonicTimestamp ProcessedMonotonicTimestamp);

public sealed record CurrentSnapshot(
    RuntimeScopeId ScopeId,
    OwnerPosition<CurrentEntry> Position,
    IReadOnlyList<CurrentEntry> Entries);

public sealed record CurrentDelta(
    RuntimeScopeId ScopeId,
    ConsumerCursor<CurrentEntry> From,
    ConsumerCursor<CurrentEntry> To,
    IReadOnlyList<CurrentEntry> Changes);
