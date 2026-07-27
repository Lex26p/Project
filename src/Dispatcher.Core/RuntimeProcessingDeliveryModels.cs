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

public enum RuntimeDownstreamStage
{
    History = 1,
    Alarm = 2,
    Event = 3,
}

public enum RuntimeDownstreamStageCommitStatus
{
    Completed = 1,
    AlreadyCompleted = 2,
}

public sealed record RuntimeDownstreamStageCommit(
    RuntimeDownstreamStageCommitStatus Status,
    RuntimeScopeId ScopeId,
    OwnerPosition<RuntimeSourceObligation> ObligationPosition,
    RuntimeDownstreamStage Stage,
    DateTimeOffset UpdatedAt);

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
