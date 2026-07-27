using Dispatcher.Semantics;

namespace Dispatcher.Core;

public sealed record RuntimeIngressLimits
{
    public RuntimeIngressLimits(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
    }

    public int Capacity { get; }
}

public enum RuntimeHostState
{
    Created = 1,
    Recovering = 2,
    Running = 3,
    Draining = 4,
    Stopped = 5,
    Faulted = 6,
}

public enum RuntimeIngressStatus
{
    Queued = 1,
    GapRecorded = 2,
}

public sealed record RuntimeIngressResult(
    RuntimeIngressStatus Status,
    RuntimeSourceObligation? GapObligation);

public enum RuntimeProcessNextStatus
{
    Idle = 1,
    Published = 2,
}

public sealed record RuntimeProcessNextResult(
    RuntimeProcessNextStatus Status,
    RuntimeSourceObligation? Obligation,
    RuntimePublicationCommit? Publication)
{
    public bool Processed => Status == RuntimeProcessNextStatus.Published;

    public static RuntimeProcessNextResult Idle { get; } = new(
        RuntimeProcessNextStatus.Idle,
        null,
        null);
}

public sealed record RuntimeReadiness(
    RuntimeHostState State,
    bool PersistenceAvailable,
    bool RecoveryComplete,
    bool ProtectedContinuity,
    bool AdmissionOpen,
    bool QueueAvailable,
    int QueuedCuts)
{
    public bool AcceptsUserMutations =>
        State == RuntimeHostState.Running &&
        PersistenceAvailable &&
        RecoveryComplete &&
        ProtectedContinuity &&
        AdmissionOpen &&
        QueueAvailable;
}

public sealed class RuntimeObligationCommitHook
{
    private readonly Func<RuntimeSourceObligation, CancellationToken, Task> callback;

    public RuntimeObligationCommitHook(
        Func<RuntimeSourceObligation, CancellationToken, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        this.callback = callback;
    }

    public Task AfterPersistedAsync(
        RuntimeSourceObligation obligation,
        CancellationToken cancellationToken) =>
        callback(obligation, cancellationToken);
}

public sealed class RuntimeDeliveryProcessor
{
    private readonly Func<RuntimeProcessingDelivery, CancellationToken, Task<Result>> callback;

    public RuntimeDeliveryProcessor(
        Func<RuntimeProcessingDelivery, CancellationToken, Task<Result>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        this.callback = callback;
    }

    public static RuntimeDeliveryProcessor NoOp { get; } = new(
        static (_, _) => Task.FromResult(Result.Success()));

    public Task<Result> ProcessAsync(
        RuntimeProcessingDelivery delivery,
        CancellationToken cancellationToken) =>
        callback(delivery, cancellationToken);
}

public enum RuntimeDeliveryCommitPoint
{
    PendingDeliveryPersisted = 1,
    DownstreamCompleted = 2,
    Published = 3,
}

public sealed class RuntimeDeliveryCommitHook
{
    private readonly Func<
        RuntimeDeliveryCommitPoint,
        RuntimeSourceObligation,
        CancellationToken,
        Task> callback;

    public RuntimeDeliveryCommitHook(
        Func<
            RuntimeDeliveryCommitPoint,
            RuntimeSourceObligation,
            CancellationToken,
            Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        this.callback = callback;
    }

    public Task AfterCommittedAsync(
        RuntimeDeliveryCommitPoint commitPoint,
        RuntimeSourceObligation obligation,
        CancellationToken cancellationToken) =>
        callback(commitPoint, obligation, cancellationToken);
}

public sealed class CoreRuntimeHost : IDisposable
{
    private const string ContinuityLostReason = "runtime.protected_continuity_lost";
    private readonly RuntimeScopeId scopeId;
    private readonly CoreRuntime runtime;
    private readonly CoreRuntimeStore store;
    private readonly RuntimeIngressLimits limits;
    private readonly RuntimeObligationCommitHook? commitHook;
    private readonly RuntimeDeliveryProcessor deliveryProcessor;
    private readonly RuntimeDeliveryCommitHook? deliveryCommitHook;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly Queue<RuntimeSourceObligation> queue = [];
    private RuntimeHostState state = RuntimeHostState.Created;
    private bool persistenceAvailable;
    private bool recoveryComplete;
    private bool protectedContinuity = true;
    private bool admissionOpen;
    private RuntimeSourceObligation? pendingGap;

    public CoreRuntimeHost(
        RuntimeScopeId scopeId,
        CoreRuntime runtime,
        CoreRuntimeStore store,
        RuntimeIngressLimits limits,
        RuntimeObligationCommitHook? commitHook = null,
        RuntimeDeliveryProcessor? deliveryProcessor = null,
        RuntimeDeliveryCommitHook? deliveryCommitHook = null)
    {
        _ = scopeId.Value;
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(limits);
        this.scopeId = scopeId;
        this.runtime = runtime;
        this.store = store;
        this.limits = limits;
        this.commitHook = commitHook;
        this.deliveryProcessor = deliveryProcessor ?? RuntimeDeliveryProcessor.NoOp;
        this.deliveryCommitHook = deliveryCommitHook;
    }

    public async Task<Result> StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state != RuntimeHostState.Created)
            {
                return Failure(
                    "runtime.lifecycle_state",
                    "Runtime host cannot start from its current state.");
            }

            state = RuntimeHostState.Recovering;
            admissionOpen = false;
            recoveryComplete = false;
            try
            {
                var recovery = await store.LoadRecoveryAsync(
                    scopeId,
                    cancellationToken).ConfigureAwait(false);
                persistenceAvailable = true;

                if (recovery.Checkpoint is not null)
                {
                    var restored = runtime.Restore(recovery.Checkpoint);
                    if (restored.IsFailure)
                    {
                        return Fault(restored);
                    }
                }

                protectedContinuity = recovery.ProtectedContinuity;

                var pendingDelivery = await store.LoadPendingDeliveryAsync(
                    scopeId,
                    cancellationToken).ConfigureAwait(false);
                if (pendingDelivery is not null)
                {
                    var completed = await CompleteDeliveryAsync(
                        pendingDelivery,
                        cancellationToken).ConfigureAwait(false);
                    if (completed.IsFailure)
                    {
                        return Fault(Result.Failure(completed.Error!));
                    }
                }

                foreach (var obligation in recovery.PendingObligations)
                {
                    var activated = runtime.ActivateBinding(obligation.Binding);
                    if (activated.IsFailure)
                    {
                        return Fault(activated);
                    }

                    var processed = await ApplyPersistAndPublishAsync(
                        obligation,
                        cancellationToken).ConfigureAwait(false);
                    if (processed.IsFailure)
                    {
                        return Fault(Result.Failure(processed.Error!));
                    }
                }

                recoveryComplete = true;
                admissionOpen = true;
                state = RuntimeHostState.Running;
                return Result.Success();
            }
            catch
            {
                persistenceAvailable = false;
                state = RuntimeHostState.Faulted;
                throw;
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public Result ActivateBinding(SourceBinding binding)
    {
        lifecycle.Wait();
        try
        {
            if (state != RuntimeHostState.Running || !admissionOpen)
            {
                return Failure(
                    "runtime.not_accepting",
                    "Runtime host is not accepting source bindings.");
            }

            return runtime.ActivateBinding(binding);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task<Result<RuntimeIngressResult>> EnqueueAsync(
        RuntimeCut cut,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cut);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state != RuntimeHostState.Running || !admissionOpen)
            {
                return Failure<RuntimeIngressResult>(
                    "runtime.not_accepting",
                    "Runtime host is not accepting source facts.");
            }

            if (cut.Binding.ScopeId != scopeId)
            {
                return Failure<RuntimeIngressResult>(
                    "runtime.scope_mismatch",
                    "RuntimeCut belongs to another host scope.");
            }

            if (queue.Count < limits.Capacity)
            {
                var obligation = await store.AppendCutAsync(
                    cut,
                    cancellationToken).ConfigureAwait(false);
                if (commitHook is not null)
                {
                    await commitHook.AfterPersistedAsync(
                        obligation,
                        cancellationToken).ConfigureAwait(false);
                }

                queue.Enqueue(obligation);
                return Result.Success(new RuntimeIngressResult(
                    RuntimeIngressStatus.Queued,
                    null));
            }

            if (cut.Observations.Count == 0)
            {
                return Failure<RuntimeIngressResult>(
                    "runtime.ingress_capacity",
                    "An empty RuntimeCut could not be admitted because bounded ingress is full.");
            }

            var positions = cut.Observations
                .Select(item => item.SourcePosition.Value)
                .ToArray();
            var gap = new RuntimeSourceGap(
                cut.Binding,
                cut.ScheduleSequence,
                positions.Min(),
                positions.Max(),
                "bounded_ingress_capacity");
            var gapObligation = await store.AppendGapAsync(
                gap,
                cancellationToken).ConfigureAwait(false);
            if (commitHook is not null)
            {
                await commitHook.AfterPersistedAsync(
                    gapObligation,
                    cancellationToken).ConfigureAwait(false);
            }

            protectedContinuity = false;
            admissionOpen = false;
            pendingGap = gapObligation;
            return Result.Success(new RuntimeIngressResult(
                RuntimeIngressStatus.GapRecorded,
                gapObligation));
        }
        catch
        {
            persistenceAvailable = false;
            admissionOpen = false;
            state = RuntimeHostState.Faulted;
            throw;
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task<Result<RuntimeProcessNextResult>> ProcessNextDeliveryAsync(
        CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state != RuntimeHostState.Running)
            {
                return Failure<RuntimeProcessNextResult>(
                    "runtime.lifecycle_state",
                    "Runtime host is not running.");
            }

            var processed = await ProcessNextCoreAsync(
                cancellationToken).ConfigureAwait(false);
            if (processed.IsFailure)
            {
                admissionOpen = false;
                state = RuntimeHostState.Faulted;
            }

            return processed;
        }
        catch
        {
            persistenceAvailable = false;
            admissionOpen = false;
            state = RuntimeHostState.Faulted;
            throw;
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task<Result<bool>> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        var processed = await ProcessNextDeliveryAsync(
            cancellationToken).ConfigureAwait(false);
        return processed.IsSuccess
            ? Result.Success(processed.Value.Processed)
            : Result.Failure<bool>(processed.Error!);
    }

    public async Task<Result> DrainAsync(
        CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state != RuntimeHostState.Running)
            {
                return Failure(
                    "runtime.lifecycle_state",
                    "Only a running runtime host can drain.");
            }

            admissionOpen = false;
            state = RuntimeHostState.Draining;
            while (queue.Count > 0 || pendingGap is not null)
            {
                var processed = await ProcessNextCoreAsync(
                    cancellationToken).ConfigureAwait(false);
                if (processed.IsFailure)
                {
                    state = RuntimeHostState.Faulted;
                    return Result.Failure(processed.Error!);
                }
            }

            state = RuntimeHostState.Stopped;
            return Result.Success();
        }
        catch
        {
            persistenceAvailable = false;
            admissionOpen = false;
            state = RuntimeHostState.Faulted;
            throw;
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public RuntimeReadiness GetReadiness()
    {
        lifecycle.Wait();
        try
        {
            return new RuntimeReadiness(
                state,
                persistenceAvailable,
                recoveryComplete,
                protectedContinuity,
                admissionOpen,
                queue.Count < limits.Capacity,
                queue.Count);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public Result EnsureUserMutationReady() =>
        GetReadiness().AcceptsUserMutations
            ? Result.Success()
            : Failure(
                "runtime.required_evidence_unavailable",
                "Runtime protected continuity and readiness evidence are required for user mutation.");

    public void Dispose() => lifecycle.Dispose();

    private async Task<Result<RuntimeProcessNextResult>> ProcessNextCoreAsync(
        CancellationToken cancellationToken)
    {
        RuntimeSourceObligation obligation;
        if (queue.Count > 0)
        {
            obligation = queue.Dequeue();
        }
        else if (pendingGap is not null)
        {
            obligation = pendingGap;
            pendingGap = null;
        }
        else
        {
            return Result.Success(RuntimeProcessNextResult.Idle);
        }

        return await ApplyPersistAndPublishAsync(
            obligation,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<RuntimeProcessNextResult>> ApplyPersistAndPublishAsync(
        RuntimeSourceObligation obligation,
        CancellationToken cancellationToken)
    {
        RuntimeCutAcceptance? acceptance = null;
        if (obligation.FactClass == RuntimeFactClass.SourceCut)
        {
            var applied = runtime.Apply(obligation.Cut!);
            if (applied.IsFailure)
            {
                return Result.Failure<RuntimeProcessNextResult>(applied.Error!);
            }

            acceptance = applied.Value;
        }
        else
        {
            var applied = runtime.ApplyGap(obligation.Gap!);
            if (applied.IsFailure)
            {
                return Result.Failure<RuntimeProcessNextResult>(applied.Error!);
            }

            protectedContinuity = false;
        }

        var saved = await store.SaveCheckpointWithPendingDeliveryAsync(
            runtime.CaptureCheckpoint(),
            obligation,
            acceptance,
            protectedContinuity,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return Result.Failure<RuntimeProcessNextResult>(saved.Error!);
        }

        if (deliveryCommitHook is not null)
        {
            await deliveryCommitHook.AfterCommittedAsync(
                RuntimeDeliveryCommitPoint.PendingDeliveryPersisted,
                obligation,
                cancellationToken).ConfigureAwait(false);
        }

        var completed = await CompleteDeliveryAsync(
            saved.Value,
            cancellationToken).ConfigureAwait(false);
        return completed.IsSuccess
            ? Result.Success(new RuntimeProcessNextResult(
                RuntimeProcessNextStatus.Published,
                obligation,
                completed.Value))
            : Result.Failure<RuntimeProcessNextResult>(completed.Error!);
    }

    private async Task<Result<RuntimePublicationCommit>> CompleteDeliveryAsync(
        RuntimeProcessingDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (delivery.Stage == RuntimeProcessingDeliveryStage.PendingDownstream)
        {
            var processed = await deliveryProcessor.ProcessAsync(
                delivery,
                cancellationToken).ConfigureAwait(false);
            if (processed.IsFailure)
            {
                return Result.Failure<RuntimePublicationCommit>(processed.Error!);
            }

            var completed = await store.CompleteDownstreamAsync(
                delivery.ScopeId,
                delivery.ObligationPosition,
                cancellationToken).ConfigureAwait(false);
            if (completed.IsFailure)
            {
                return Result.Failure<RuntimePublicationCommit>(completed.Error!);
            }

            if (deliveryCommitHook is not null)
            {
                await deliveryCommitHook.AfterCommittedAsync(
                    RuntimeDeliveryCommitPoint.DownstreamCompleted,
                    delivery.Obligation,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var publication = await store.PublishCompletedDeliveryAsync(
            delivery.ScopeId,
            delivery.ObligationPosition,
            runtime.GetCurrentCapacity().RetainedChangeCapacity,
            ready: protectedContinuity,
            degradationReasonCode: protectedContinuity
                ? null
                : ContinuityLostReason,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (publication.IsFailure)
        {
            return publication;
        }

        if (deliveryCommitHook is not null)
        {
            await deliveryCommitHook.AfterCommittedAsync(
                RuntimeDeliveryCommitPoint.Published,
                delivery.Obligation,
                cancellationToken).ConfigureAwait(false);
        }

        return publication;
    }

    private Result Fault(Result result)
    {
        state = RuntimeHostState.Faulted;
        admissionOpen = false;
        return result;
    }

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));
}
