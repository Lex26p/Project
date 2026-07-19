using Dispatcher.Semantics;

namespace Dispatcher.Core;

public sealed record RuntimeCurrentLimits
{
    public RuntimeCurrentLimits(int maxPoints, int retainedChangeCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPoints);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedChangeCapacity);
        MaxPoints = maxPoints;
        RetainedChangeCapacity = retainedChangeCapacity;
    }

    public int MaxPoints { get; }

    public int RetainedChangeCapacity { get; }
}

public sealed record RuntimeCurrentCapacity(
    int PointCount,
    int PointCapacity,
    int RetainedChangeCount,
    int RetainedChangeCapacity);

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
