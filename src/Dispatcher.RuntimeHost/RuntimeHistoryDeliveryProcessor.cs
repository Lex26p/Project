using Dispatcher.Core;
using Dispatcher.History;
using Dispatcher.Semantics;

namespace Dispatcher.RuntimeHost;

public enum RuntimeHistoryDeliveryStatus
{
    Accepted = 1,
    Duplicate = 2,
    AlreadyCompleted = 3,
}

public sealed record RuntimeHistoryDeliveryResult(
    RuntimeHistoryDeliveryStatus Status,
    RuntimeScopeId ScopeId,
    OwnerPosition<RuntimeSourceObligation> ObligationPosition,
    HistoryIngestAcceptance? Acceptance);

public sealed class RuntimeHistoryDeliveryProcessor
{
    private readonly HistoryStore historyStore;
    private readonly CoreRuntimeStore coreStore;

    public RuntimeHistoryDeliveryProcessor(
        HistoryStore historyStore,
        CoreRuntimeStore coreStore)
    {
        ArgumentNullException.ThrowIfNull(historyStore);
        ArgumentNullException.ThrowIfNull(coreStore);
        this.historyStore = historyStore;
        this.coreStore = coreStore;
    }

    public async Task<Result<RuntimeHistoryDeliveryResult>> ProcessAsync(
        RuntimeProcessingDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        if (delivery.HistoryState == RuntimeDownstreamDeliveryState.Completed)
        {
            return Result.Success(new RuntimeHistoryDeliveryResult(
                RuntimeHistoryDeliveryStatus.AlreadyCompleted,
                delivery.ScopeId,
                delivery.ObligationPosition,
                null));
        }

        if (delivery.Stage != RuntimeProcessingDeliveryStage.PendingDownstream)
        {
            return Failure(
                "runtime.delivery_downstream_closed",
                "The processing delivery no longer accepts History delivery.");
        }

        var accepted = await historyStore.AcceptAsync(
            delivery.Obligation,
            cancellationToken).ConfigureAwait(false);
        if (accepted.IsFailure)
        {
            var recorded = await coreStore.RecordDownstreamFailureAsync(
                delivery.ScopeId,
                delivery.ObligationPosition,
                RuntimeDownstreamStage.History,
                accepted.Error!.Code.Value,
                cancellationToken).ConfigureAwait(false);
            return recorded.IsFailure
                ? Result.Failure<RuntimeHistoryDeliveryResult>(recorded.Error!)
                : Result.Failure<RuntimeHistoryDeliveryResult>(accepted.Error!);
        }

        var committed = await coreStore.CompleteDownstreamStageAsync(
            delivery.ScopeId,
            delivery.ObligationPosition,
            RuntimeDownstreamStage.History,
            cancellationToken).ConfigureAwait(false);
        if (committed.IsFailure)
        {
            return Result.Failure<RuntimeHistoryDeliveryResult>(committed.Error!);
        }

        var status = committed.Value.Status ==
            RuntimeDownstreamStageCommitStatus.AlreadyCompleted
                ? RuntimeHistoryDeliveryStatus.AlreadyCompleted
                : accepted.Value.Status == HistoryIngestStatus.Accepted
                    ? RuntimeHistoryDeliveryStatus.Accepted
                    : RuntimeHistoryDeliveryStatus.Duplicate;
        return Result.Success(new RuntimeHistoryDeliveryResult(
            status,
            delivery.ScopeId,
            delivery.ObligationPosition,
            accepted.Value));
    }

    private static Result<RuntimeHistoryDeliveryResult> Failure(
        string code,
        string message) =>
        Result.Failure<RuntimeHistoryDeliveryResult>(
            new OperationError(
                ErrorCode.From(code),
                message));
}
