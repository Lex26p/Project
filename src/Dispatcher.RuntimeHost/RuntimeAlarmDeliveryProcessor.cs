using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Semantics;

namespace Dispatcher.RuntimeHost;

public enum RuntimeAlarmDeliveryStatus
{
    Evaluated = 1,
    AlreadyCompleted = 2,
    GapSkipped = 3,
}

public sealed record RuntimeAlarmDeliveryResult(
    RuntimeAlarmDeliveryStatus Status,
    RuntimeScopeId ScopeId,
    OwnerPosition<RuntimeSourceObligation> ObligationPosition,
    Guid? ConfigurationRevisionId,
    RevisionNumber? DefinitionEpoch,
    AlarmEvaluationSnapshot? Evaluation);

public sealed class RuntimeAlarmEvaluationCommitHook
{
    private readonly Func<
        AlarmEvaluationSnapshot,
        CancellationToken,
        Task> callback;

    public RuntimeAlarmEvaluationCommitHook(
        Func<
            AlarmEvaluationSnapshot,
            CancellationToken,
            Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        this.callback = callback;
    }

    public Task AfterCommittedAsync(
        AlarmEvaluationSnapshot evaluation,
        CancellationToken cancellationToken) =>
        callback(evaluation, cancellationToken);
}

public sealed class RuntimeAlarmDeliveryProcessor
{
    private readonly AlarmStore alarmStore;
    private readonly CoreRuntimeStore coreStore;
    private readonly RuntimeAlarmEvaluationCommitHook? commitHook;

    public RuntimeAlarmDeliveryProcessor(
        AlarmStore alarmStore,
        CoreRuntimeStore coreStore,
        RuntimeAlarmEvaluationCommitHook? commitHook = null)
    {
        ArgumentNullException.ThrowIfNull(alarmStore);
        ArgumentNullException.ThrowIfNull(coreStore);
        this.alarmStore = alarmStore;
        this.coreStore = coreStore;
        this.commitHook = commitHook;
    }

    public async Task<Result<RuntimeAlarmDeliveryResult>> ProcessAsync(
        RuntimeProcessingDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        if (delivery.AlarmState == RuntimeDownstreamDeliveryState.Completed)
        {
            return Result.Success(new RuntimeAlarmDeliveryResult(
                RuntimeAlarmDeliveryStatus.AlreadyCompleted,
                delivery.ScopeId,
                delivery.ObligationPosition,
                delivery.DefinitionEpoch,
                delivery.AlarmDefinitionEpoch,
                null));
        }

        if (delivery.Stage != RuntimeProcessingDeliveryStage.PendingDownstream)
        {
            return Failure(
                "runtime.delivery_downstream_closed",
                "The processing delivery no longer accepts Alarm delivery.");
        }

        if (delivery.HistoryState != RuntimeDownstreamDeliveryState.Completed)
        {
            return Failure(
                "runtime.delivery_stage_order",
                "History delivery must complete before Alarm evaluation.");
        }

        if (delivery.Obligation.FactClass == RuntimeFactClass.SourceGap)
        {
            var skipped = await coreStore.CompleteDownstreamStageAsync(
                delivery.ScopeId,
                delivery.ObligationPosition,
                RuntimeDownstreamStage.Alarm,
                cancellationToken).ConfigureAwait(false);
            return skipped.IsFailure
                ? Result.Failure<RuntimeAlarmDeliveryResult>(skipped.Error!)
                : Result.Success(new RuntimeAlarmDeliveryResult(
                    RuntimeAlarmDeliveryStatus.GapSkipped,
                    delivery.ScopeId,
                    delivery.ObligationPosition,
                    delivery.DefinitionEpoch,
                    delivery.AlarmDefinitionEpoch,
                    null));
        }

        if (delivery.PostCutAcceptance is null)
        {
            return await RecordFailureAsync(
                delivery,
                new OperationError(
                    ErrorCode.From("runtime.delivery_alarm_acceptance"),
                    "A cut delivery requires its exact post-cut acceptance for Alarm evaluation."),
                cancellationToken).ConfigureAwait(false);
        }

        if (delivery.DefinitionEpoch is null ||
            delivery.DefinitionEpoch.Value == Guid.Empty ||
            delivery.AlarmDefinitionEpoch is null)
        {
            return await RecordFailureAsync(
                delivery,
                new OperationError(
                    ErrorCode.From("runtime.delivery_alarm_epoch_missing"),
                    "A cut delivery requires configuration revision identity and Alarm definition epoch."),
                cancellationToken).ConfigureAwait(false);
        }

        var recovery = await coreStore.LoadRecoveryAsync(
            delivery.ScopeId,
            cancellationToken).ConfigureAwait(false);
        if (recovery.Checkpoint is null ||
            recovery.CheckpointObligationPosition != delivery.ObligationPosition.Value)
        {
            return await RecordFailureAsync(
                delivery,
                new OperationError(
                    ErrorCode.From("runtime.delivery_alarm_checkpoint"),
                    "The durable Core checkpoint does not match the pending Alarm delivery."),
                cancellationToken).ConfigureAwait(false);
        }

        var checkpoint = recovery.Checkpoint;
        var postCutSnapshot = new CurrentSnapshot(
            delivery.ScopeId,
            checkpoint.CurrentPosition,
            checkpoint.Current);
        var evaluator = new AlarmEvaluator(
            delivery.ScopeId,
            delivery.AlarmDefinitionEpoch.Value,
            alarmStore);
        var evaluated = await evaluator.EvaluatePostRuntimeCutAsync(
            delivery.PostCutAcceptance,
            postCutSnapshot,
            cancellationToken).ConfigureAwait(false);
        if (evaluated.IsFailure)
        {
            return await RecordFailureAsync(
                delivery,
                evaluated.Error!,
                cancellationToken).ConfigureAwait(false);
        }

        if (commitHook is not null)
        {
            await commitHook.AfterCommittedAsync(
                evaluated.Value,
                cancellationToken).ConfigureAwait(false);
        }

        var committed = await coreStore.CompleteDownstreamStageAsync(
            delivery.ScopeId,
            delivery.ObligationPosition,
            RuntimeDownstreamStage.Alarm,
            cancellationToken).ConfigureAwait(false);
        if (committed.IsFailure)
        {
            return Result.Failure<RuntimeAlarmDeliveryResult>(committed.Error!);
        }

        return Result.Success(new RuntimeAlarmDeliveryResult(
            committed.Value.Status ==
                RuntimeDownstreamStageCommitStatus.AlreadyCompleted
                    ? RuntimeAlarmDeliveryStatus.AlreadyCompleted
                    : RuntimeAlarmDeliveryStatus.Evaluated,
            delivery.ScopeId,
            delivery.ObligationPosition,
            delivery.DefinitionEpoch,
            delivery.AlarmDefinitionEpoch,
            evaluated.Value));
    }

    private async Task<Result<RuntimeAlarmDeliveryResult>> RecordFailureAsync(
        RuntimeProcessingDelivery delivery,
        OperationError error,
        CancellationToken cancellationToken)
    {
        var recorded = await coreStore.RecordDownstreamFailureAsync(
            delivery.ScopeId,
            delivery.ObligationPosition,
            RuntimeDownstreamStage.Alarm,
            error.Code.Value,
            cancellationToken).ConfigureAwait(false);
        return recorded.IsFailure
            ? Result.Failure<RuntimeAlarmDeliveryResult>(recorded.Error!)
            : Result.Failure<RuntimeAlarmDeliveryResult>(error);
    }

    private static Result<RuntimeAlarmDeliveryResult> Failure(
        string code,
        string message) =>
        Result.Failure<RuntimeAlarmDeliveryResult>(
            new OperationError(
                ErrorCode.From(code),
                message));
}
