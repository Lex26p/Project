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

    public RuntimeObligationCommitHook(Func<RuntimeSourceObligation, CancellationToken, Task> callback)
    {
        this.callback = callback;
    }

    public Task AfterPersistedAsync(
        RuntimeSourceObligation obligation,
        CancellationToken cancellationToken) =>
        callback(obligation, cancellationToken);
}

public sealed class CoreRuntimeHost : IDisposable
{
    private readonly RuntimeScopeId scopeId;
    private readonly CoreRuntime runtime;
    private readonly CoreRuntimeStore store;
    private readonly RuntimeIngressLimits limits;
    private readonly RuntimeObligationCommitHook? commitHook;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly Queue<RuntimeSourceObligation> queue = [];
    private RuntimeHostState state = RuntimeHostState.Created;
    private bool persistenceAvailable;
    private bool recoveryComplete;
    private bool protectedContinuity = true;
    private bool admissionOpen;
    private ulong checkpointPosition;
    private RuntimeSourceObligation? pendingGap;

    public CoreRuntimeHost(
        RuntimeScopeId scopeId,
        CoreRuntime runtime,
        CoreRuntimeStore store,
        RuntimeIngressLimits limits,
        RuntimeObligationCommitHook? commitHook = null)
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
    }

    public async Task<Result> StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state != RuntimeHostState.Created)
            {
                return Failure("runtime.lifecycle_state", "Runtime host cannot start from its current state.");
            }

            state = RuntimeHostState.Recovering;
            admissionOpen = false;
            recoveryComplete = false;
            RuntimeRecoveryState recovery;
            try
            {
                recovery = await store.LoadRecoveryAsync(scopeId, cancellationToken).ConfigureAwait(false);
                persistenceAvailable = true;
            }
            catch
            {
                persistenceAvailable = false;
                state = RuntimeHostState.Faulted;
                throw;
            }

            if (recovery.Checkpoint is not null)
            {
                var restored = runtime.Restore(recovery.Checkpoint);
                if (restored.IsFailure)
                {
                    state = RuntimeHostState.Faulted;
                    return restored;
                }
            }

            checkpointPosition = recovery.CheckpointObligationPosition;
            protectedContinuity = recovery.ProtectedContinuity;
            foreach (var obligation in recovery.PendingObligations)
            {
                var activated = runtime.ActivateBinding(obligation.Binding);
                if (activated.IsFailure)
                {
                    state = RuntimeHostState.Faulted;
                    return activated;
                }

                var replayed = obligation.FactClass == RuntimeFactClass.SourceCut
                    ? runtime.Apply(obligation.Cut!).IsSuccess
                        ? Result.Success()
                        : Failure("runtime.replay_failed", "A protected RuntimeCut could not be replayed.")
                    : runtime.ApplyGap(obligation.Gap!);
                if (replayed.IsFailure)
                {
                    state = RuntimeHostState.Faulted;
                    return replayed;
                }

                if (obligation.FactClass == RuntimeFactClass.SourceGap)
                {
                    protectedContinuity = false;
                }

                checkpointPosition = obligation.Position.Value;
            }

            if (recovery.PendingObligations.Count > 0)
            {
                var checkpointed = await store.SaveCheckpointAsync(
                    runtime.CaptureCheckpoint(),
                    checkpointPosition,
                    protectedContinuity,
                    cancellationToken).ConfigureAwait(false);
                if (checkpointed.IsFailure)
                {
                    state = RuntimeHostState.Faulted;
                    return checkpointed;
                }
            }

            recoveryComplete = true;
            admissionOpen = true;
            state = RuntimeHostState.Running;
            return Result.Success();
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
                return Failure("runtime.not_accepting", "Runtime host is not accepting source bindings.");
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
                return Failure<RuntimeIngressResult>("runtime.scope_mismatch", "RuntimeCut belongs to another host scope.");
            }

            if (queue.Count < limits.Capacity)
            {
                var obligation = await store.AppendCutAsync(cut, cancellationToken).ConfigureAwait(false);
                if (commitHook is not null)
                {
                    await commitHook.AfterPersistedAsync(obligation, cancellationToken).ConfigureAwait(false);
                }

                queue.Enqueue(obligation);
                return Result.Success(new RuntimeIngressResult(RuntimeIngressStatus.Queued, null));
            }

            if (cut.Observations.Count == 0)
            {
                return Failure<RuntimeIngressResult>(
                    "runtime.ingress_capacity",
                    "An empty RuntimeCut could not be admitted because bounded ingress is full.");
            }

            var positions = cut.Observations.Select(item => item.SourcePosition.Value).ToArray();
            var gap = new RuntimeSourceGap(
                cut.Binding,
                cut.ScheduleSequence,
                positions.Min(),
                positions.Max(),
                "bounded_ingress_capacity");
            var gapObligation = await store.AppendGapAsync(gap, cancellationToken).ConfigureAwait(false);
            if (commitHook is not null)
            {
                await commitHook.AfterPersistedAsync(gapObligation, cancellationToken).ConfigureAwait(false);
            }

            protectedContinuity = false;
            admissionOpen = false;
            pendingGap = gapObligation;
            return Result.Success(new RuntimeIngressResult(RuntimeIngressStatus.GapRecorded, gapObligation));
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

    public async Task<Result<bool>> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state != RuntimeHostState.Running)
            {
                return Failure<bool>("runtime.lifecycle_state", "Runtime host is not running.");
            }

            var processed = await ProcessNextCoreAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<Result> DrainAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state != RuntimeHostState.Running)
            {
                return Failure("runtime.lifecycle_state", "Only a running runtime host can drain.");
            }

            admissionOpen = false;
            state = RuntimeHostState.Draining;
            while (queue.Count > 0 || pendingGap is not null)
            {
                var processed = await ProcessNextCoreAsync(cancellationToken).ConfigureAwait(false);
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

    public Result EnsureUserMutationReady() => GetReadiness().AcceptsUserMutations
        ? Result.Success()
        : Failure(
            "runtime.required_evidence_unavailable",
            "Runtime protected continuity and readiness evidence are required for user mutation.");

    public void Dispose() => lifecycle.Dispose();

    private async Task<Result<bool>> ProcessNextCoreAsync(CancellationToken cancellationToken)
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
            return Result.Success(false);
        }

        Result applied;
        if (obligation.FactClass == RuntimeFactClass.SourceCut)
        {
            var cutApplied = runtime.Apply(obligation.Cut!);
            applied = cutApplied.IsSuccess ? Result.Success() : Result.Failure(cutApplied.Error!);
        }
        else
        {
            applied = runtime.ApplyGap(obligation.Gap!);
        }
        if (applied.IsFailure)
        {
            return Result.Failure<bool>(applied.Error!);
        }

        checkpointPosition = obligation.Position.Value;
        var checkpointed = await store.SaveCheckpointAsync(
            runtime.CaptureCheckpoint(),
            checkpointPosition,
            protectedContinuity,
            cancellationToken).ConfigureAwait(false);
        return checkpointed.IsSuccess
            ? Result.Success(true)
            : Result.Failure<bool>(checkpointed.Error!);
    }

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));
}
