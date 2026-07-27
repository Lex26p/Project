using Dispatcher.Semantics;

namespace Dispatcher.Core;

public enum RuntimeProcessingDeliveryStage
{
    PendingDownstream = 1,
    DownstreamCompleted = 2,
    Published = 3,
}

public enum RuntimeDownstreamDeliveryState
{
    Pending = 1,
    Completed = 2,
}

public enum RuntimePublicationCommitStatus
{
    Published = 1,
    AlreadyPublished = 2,
}

public sealed record RuntimePublicationCommit(
    RuntimePublicationCommitStatus Status,
    RuntimeScopeId ScopeId,
    OwnerPosition<RuntimeSourceObligation> ObligationPosition,
    ConsumerCursor<CurrentEntry> CurrentCursor,
    int PublishedTransitionCount,
    int RetainedDeltaCount);

public sealed record RuntimeProcessingDelivery(
    RuntimeSourceObligation Obligation,
    RuntimeProcessingDeliveryStage Stage,
    RuntimeCutAcceptance? PostCutAcceptance,
    string? GapReason,
    Guid? DefinitionEpoch,
    RuntimeDownstreamDeliveryState HistoryState,
    RuntimeDownstreamDeliveryState AlarmState,
    RuntimeDownstreamDeliveryState EventState,
    string? LastErrorCode,
    DateTimeOffset? LastErrorAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DownstreamCompletedAt,
    DateTimeOffset? PublishedAt)
{
    public RuntimeScopeId ScopeId => Obligation.ScopeId;

    public OwnerPosition<RuntimeSourceObligation> ObligationPosition => Obligation.Position;
}
