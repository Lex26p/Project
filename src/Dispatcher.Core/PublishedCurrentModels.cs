using Dispatcher.Semantics;

namespace Dispatcher.Core;

public sealed record PublishedCurrentReadLimits
{
    public PublishedCurrentReadLimits(int maxSnapshotPoints, int maxDeltaChanges)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSnapshotPoints);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDeltaChanges);
        MaxSnapshotPoints = maxSnapshotPoints;
        MaxDeltaChanges = maxDeltaChanges;
    }

    public int MaxSnapshotPoints { get; }

    public int MaxDeltaChanges { get; }
}

public sealed class PublishedCurrentReadLimitExceededException : InvalidOperationException
{
    public PublishedCurrentReadLimitExceededException()
        : base("Published current read exceeded its configured capacity.")
    {
    }
}

public sealed record PublishedCurrentEntry(
    RuntimeScopeId ScopeId,
    SourceId SourceId,
    PointId PointId,
    SourceBindingGeneration BindingGeneration,
    SourceSessionGeneration SessionGeneration,
    OwnerPosition<SourceObservation> SourcePosition,
    OwnerPosition<PublishedCurrentEntry> CurrentPosition,
    TypedValue<decimal> Value,
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
    public const int CurrentMeasurementSemanticVersion = 2;

    public int MeasurementSemanticVersion { get; init; } =
        CurrentMeasurementSemanticVersion;

    public bool CanServeCurrent =>
        Published &&
        Ready &&
        ProtectedContinuity &&
        MeasurementSemanticVersion == CurrentMeasurementSemanticVersion;
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
    ConsumerCursor<PublishedCurrentEntry> To,
    PublishedCurrentDeltaStatus Status,
    IReadOnlyList<PublishedCurrentEntry> Changes)
{
    public RuntimeScopeId ScopeId => Readiness.ScopeId;

    public bool RequiresSnapshot =>
        Status is PublishedCurrentDeltaStatus.ScopeNotPublished or
            PublishedCurrentDeltaStatus.CursorTooOld;
}
