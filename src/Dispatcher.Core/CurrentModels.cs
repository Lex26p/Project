using Dispatcher.Semantics;

namespace Dispatcher.Core;

public sealed record CurrentEntry(
    RuntimeScopeId ScopeId,
    SourceId SourceId,
    PointId PointId,
    SourceBindingGeneration BindingGeneration,
    SourceSessionGeneration SessionGeneration,
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

public sealed record SourceLiveness(
    RuntimeScopeId ScopeId,
    SourceId SourceId,
    SourceBindingGeneration BindingGeneration,
    SourceSessionGeneration SessionGeneration,
    ulong ScheduleSequence,
    ulong LastSourcePosition,
    int ObservationCount,
    OwnerPosition<SourceLiveness> LivenessPosition,
    ReceiveTimestamp ReceiveTimestamp,
    ProcessedTimestamp ProcessedTimestamp,
    MonotonicTimestamp ProcessedMonotonicTimestamp);

public sealed record SourceLivenessSnapshot(
    RuntimeScopeId ScopeId,
    OwnerPosition<SourceLiveness> Position,
    IReadOnlyList<SourceLiveness> Sources);

public sealed record RuntimeCutAcceptance(
    RuntimeCut Cut,
    IReadOnlyList<CurrentEntry> CurrentTransitions,
    SourceLiveness Liveness);
