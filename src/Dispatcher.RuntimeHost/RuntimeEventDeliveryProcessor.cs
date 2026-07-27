using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Semantics;

namespace Dispatcher.RuntimeHost;

public enum RuntimeEventDeliveryStatus
{
    Projected = 1,
    Duplicate = 2,
    AlreadyCompleted = 3,
    GapSkipped = 4,
    NoOccurrences = 5,
}

public sealed record RuntimeEventDeliveryResult(
    RuntimeEventDeliveryStatus Status,
    RuntimeScopeId ScopeId,
    OwnerPosition<RuntimeSourceObligation> ObligationPosition,
    IReadOnlyList<EventAcceptanceResult> Acceptances);

public sealed class RuntimeEventAcceptanceCommitHook
{
    private readonly Func<
        EventAcceptanceResult,
        CancellationToken,
        Task> callback;

    public RuntimeEventAcceptanceCommitHook(
        Func<
            EventAcceptanceResult,
            CancellationToken,
            Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        this.callback = callback;
    }

    public Task AfterCommittedAsync(
        EventAcceptanceResult acceptance,
        CancellationToken cancellationToken) =>
        callback(acceptance, cancellationToken);
}

public sealed class RuntimeEventDeliveryProcessor
{
    private readonly AlarmStore alarmStore;
    private readonly EventStore eventStore;
    private readonly CoreRuntimeStore coreStore;
    private readonly RuntimeEventAcceptanceCommitHook? commitHook;

    public RuntimeEventDeliveryProcessor(
        AlarmStore alarmStore,
        EventStore eventStore,
        CoreRuntimeStore coreStore,
        RuntimeEventAcceptanceCommitHook? commitHook = null)
    {
        ArgumentNullException.ThrowIfNull(alarmStore);
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(coreStore);
        this.alarmStore = alarmStore;
        this.eventStore = eventStore;
        this.coreStore = coreStore;
        this.commitHook = commitHook;
    }

    public async Task<Result<RuntimeEventDeliveryResult>> ProcessAsync(
        RuntimeProcessingDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        if (delivery.EventState == RuntimeDownstreamDeliveryState.Completed)
        {
            return Result.Success(new RuntimeEventDeliveryResult(
                RuntimeEventDeliveryStatus.AlreadyCompleted,
                delivery.ScopeId,
                delivery.ObligationPosition,
                []));
        }

        if (delivery.Stage != RuntimeProcessingDeliveryStage.PendingDownstream)
        {
            return Failure(
                "runtime.delivery_downstream_closed",
                "The processing delivery no longer accepts Event delivery.");
        }

        if (delivery.HistoryState != RuntimeDownstreamDeliveryState.Completed ||
            delivery.AlarmState != RuntimeDownstreamDeliveryState.Completed)
        {
            return Failure(
                "runtime.delivery_stage_order",
                "History and Alarm delivery must complete before Event projection.");
        }

        if (delivery.Obligation.FactClass == RuntimeFactClass.SourceGap)
        {
            return await CompleteAsync(
                delivery,
                RuntimeEventDeliveryStatus.GapSkipped,
                [],
                cancellationToken).ConfigureAwait(false);
        }

        var occurrences = await alarmStore
            .ReadOccurrencesAsync(
                delivery.ScopeId,
                cancellationToken)
            .ConfigureAwait(false);
        if (occurrences.Count == 0)
        {
            return await CompleteAsync(
                delivery,
                RuntimeEventDeliveryStatus.NoOccurrences,
                [],
                cancellationToken).ConfigureAwait(false);
        }

        var acceptances = new List<EventAcceptanceResult>(occurrences.Count);
        foreach (var occurrence in occurrences
                     .OrderBy(item => item.OccurrenceId.Value))
        {
            var accepted = await eventStore
                .AcceptAlarmOccurrenceAsync(
                    occurrence,
                    cancellationToken)
                .ConfigureAwait(false);
            if (accepted.IsFailure)
            {
                return await RecordFailureAsync(
                    delivery,
                    accepted.Error!,
                    cancellationToken).ConfigureAwait(false);
            }

            acceptances.Add(accepted.Value);
            if (commitHook is not null)
            {
                await commitHook.AfterCommittedAsync(
                    accepted.Value,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var status = acceptances.Any(item =>
            item.EventInserted || item.ProjectionChanged)
                ? RuntimeEventDeliveryStatus.Projected
                : RuntimeEventDeliveryStatus.Duplicate;
        return await CompleteAsync(
            delivery,
            status,
            acceptances,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<RuntimeEventDeliveryResult>> CompleteAsync(
        RuntimeProcessingDelivery delivery,
        RuntimeEventDeliveryStatus status,
        IReadOnlyList<EventAcceptanceResult> acceptances,
        CancellationToken cancellationToken)
    {
        var completed = await coreStore.CompleteDownstreamStageAsync(
            delivery.ScopeId,
            delivery.ObligationPosition,
            RuntimeDownstreamStage.Event,
            cancellationToken).ConfigureAwait(false);
        if (completed.IsFailure)
        {
            return Result.Failure<RuntimeEventDeliveryResult>(
                completed.Error!);
        }

        return Result.Success(new RuntimeEventDeliveryResult(
            completed.Value.Status ==
                RuntimeDownstreamStageCommitStatus.AlreadyCompleted
                    ? RuntimeEventDeliveryStatus.AlreadyCompleted
                    : status,
            delivery.ScopeId,
            delivery.ObligationPosition,
            acceptances));
    }

    private async Task<Result<RuntimeEventDeliveryResult>> RecordFailureAsync(
        RuntimeProcessingDelivery delivery,
        OperationError error,
        CancellationToken cancellationToken)
    {
        var recorded = await coreStore.RecordDownstreamFailureAsync(
            delivery.ScopeId,
            delivery.ObligationPosition,
            RuntimeDownstreamStage.Event,
            error.Code.Value,
            cancellationToken).ConfigureAwait(false);
        return recorded.IsFailure
            ? Result.Failure<RuntimeEventDeliveryResult>(recorded.Error!)
            : Result.Failure<RuntimeEventDeliveryResult>(error);
    }

    private static Result<RuntimeEventDeliveryResult> Failure(
        string code,
        string message) =>
        Result.Failure<RuntimeEventDeliveryResult>(
            new OperationError(
                ErrorCode.From(code),
                message));
}
