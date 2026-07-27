using Dispatcher.Semantics;

namespace Dispatcher.Core;

public sealed record PublishedCurrentEntry(
    RuntimeScopeId ScopeId,
    SourceId SourceId,
    PointId PointId,
    SourceBindingGeneration BindingGeneration,
    SourceSessionGeneration SessionGeneration,
    OwnerPosition<SourceObservation> SourcePosition,
    OwnerPosition<PublishedCurrentEntry> CurrentPosition,
    TypedValue<long> Value,
    Unit Unit,
    DataQuality Quality,
    Freshness Freshness,
    SourceTimestamp SourceTimestamp,
    ReceiveTimestamp ReceiveTimestamp,
    ProcessedTimestamp ProcessedTimestamp);

public sealed record PublishedRuntimeReadiness(
    RuntimeScopeId ScopeId,
    bool Published,
    OwnerPosition<RuntimeSourceObligation> CompletedObligationPosition,
    ConsumerCursor<PublishedCurrentEntry> CurrentCursor,
    ConsumerCursor<PublishedCurrentEntry> EarliestResumableCursor,
    bool ProtectedContinuity,
    bool Ready,
    string? DegradationReasonCode,
    DateTimeOffset? HeartbeatAt,
    DateTimeOffset? PublishedAt)
{
    public bool CanServeCurrent =>
        Published &&
        Ready &&
        ProtectedContinuity;
}

public sealed record PublishedCurrentSnapshot(
    PublishedRuntimeReadiness Readiness,
    IReadOnlyList<PublishedCurrentEntry> Entries)
{
    public RuntimeScopeId ScopeId => Readiness.ScopeId;

    public ConsumerCursor<PublishedCurrentEntry> Cursor =>
        Readiness.CurrentCursor;
}

public enum PublishedCurrentDeltaStatus
{
    Available = 1,
    ScopeNotPublished = 2,
    CursorTooOld = 3,
    CursorAhead = 4,
}

public sealed record PublishedCurrentDelta(
    PublishedRuntimeReadiness Readiness,
    ConsumerCursor<PublishedCurrentEntry> RequestedCursor,
    PublishedCurrentDeltaStatus Status,
    IReadOnlyList<PublishedCurrentEntry> Changes)
{
    public RuntimeScopeId ScopeId => Readiness.ScopeId;

    public ConsumerCursor<PublishedCurrentEntry> To =>
        Readiness.CurrentCursor;

    public bool RequiresSnapshot =>
        Status is PublishedCurrentDeltaStatus.ScopeNotPublished or
            PublishedCurrentDeltaStatus.CursorTooOld;
}
