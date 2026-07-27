using Dispatcher.Core;
using Dispatcher.Semantics;
using Dispatcher.Simulator;

namespace Dispatcher.RuntimeHost;

public delegate Result RuntimeSourceBindingActivator(SourceBinding binding);

public delegate Task<Result<RuntimeIngressResult>> RuntimeCutEnqueuer(
    RuntimeCut cut,
    CancellationToken cancellationToken);

public delegate Task<Result<bool>> RuntimeObligationProcessor(CancellationToken cancellationToken);

public delegate Task RuntimeWorkerDelay(TimeSpan delay, CancellationToken cancellationToken);

public enum SimulatorPollingWorkerState
{
    Created = 1,
    Running = 2,
    Stopped = 3,
    Faulted = 4,
}

public sealed record SimulatorPollingWorkerSnapshot(
    SimulatorPollingWorkerState State,
    SourceBinding Binding,
    ulong ScheduleSequence,
    ulong CompletedPolls,
    ulong TimedOutPolls,
    ulong StalePolls,
    ulong MissedOverlapPolls,
    ulong MissedCapacityPolls,
    ulong AdmittedCuts,
    ulong ProcessedObligations,
    string? LastErrorCode,
    PollSchedulerSnapshot Scheduler);

public sealed class SimulatorPollingWorker
{
    private readonly SimulatorPollingSource source;
    private readonly BoundedPollScheduler scheduler;
    private readonly TimeSpan pollInterval;
    private readonly int maxProcessBatch;
    private readonly RuntimeSourceBindingActivator activateBinding;
    private readonly RuntimeCutEnqueuer enqueueCut;
    private readonly RuntimeObligationProcessor processNext;
    private readonly RuntimeWorkerDelay delay;
    private readonly object errorSync = new();
    private int runClaimed;
    private int state = (int)SimulatorPollingWorkerState.Created;
    private long scheduleSequence;
    private long completedPolls;
    private long timedOutPolls;
    private long stalePolls;
    private long missedOverlapPolls;
    private long missedCapacityPolls;
    private long admittedCuts;
    private long processedObligations;
    private string? lastErrorCode;

    public SimulatorPollingWorker(
        SimulatorPollingSource source,
        BoundedPollScheduler scheduler,
        TimeSpan pollInterval,
        int maxProcessBatch,
        RuntimeSourceBindingActivator activateBinding,
        RuntimeCutEnqueuer enqueueCut,
        RuntimeObligationProcessor processNext,
        RuntimeWorkerDelay? delay = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxProcessBatch);
        ArgumentNullException.ThrowIfNull(activateBinding);
        ArgumentNullException.ThrowIfNull(enqueueCut);
        ArgumentNullException.ThrowIfNull(processNext);
        this.source = source;
        this.scheduler = scheduler;
        this.pollInterval = pollInterval;
        this.maxProcessBatch = maxProcessBatch;
        this.activateBinding = activateBinding;
        this.enqueueCut = enqueueCut;
        this.processNext = processNext;
        this.delay = delay ?? Task.Delay;
    }

    public async Task<Result> RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref runClaimed, 1, 0) != 0)
        {
            return Failure("runtime.simulator_worker_state", "Simulator polling worker can only run once.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var schedulerActivated = scheduler.ActivateBinding(source.Binding);
            if (schedulerActivated.IsFailure)
            {
                return Fault(schedulerActivated.Error!);
            }

            var runtimeActivated = activateBinding(source.Binding);
            if (runtimeActivated.IsFailure)
            {
                return Fault(runtimeActivated.Error!);
            }

            Volatile.Write(ref state, (int)SimulatorPollingWorkerState.Running);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sequence = checked((ulong)Interlocked.Increment(ref scheduleSequence));
                var started = scheduler.TryStart(source.Binding, sequence);
                if (started.IsFailure)
                {
                    return Fault(started.Error!);
                }

                var handled = await HandleStartAsync(started.Value, cancellationToken).ConfigureAwait(false);
                if (handled.IsFailure)
                {
                    return Fault(handled.Error!);
                }

                await delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref state, (int)SimulatorPollingWorkerState.Stopped);
            return Result.Success();
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return Fault(new OperationError(
                ErrorCode.From("runtime.simulator_poll_failed"),
                "Simulator polling failed without exposing configuration or runtime data."));
        }
    }

    public SimulatorPollingWorkerSnapshot GetSnapshot()
    {
        string? errorCode;
        lock (errorSync)
        {
            errorCode = lastErrorCode;
        }

        return new SimulatorPollingWorkerSnapshot(
            (SimulatorPollingWorkerState)Volatile.Read(ref state),
            source.Binding,
            checked((ulong)Volatile.Read(ref scheduleSequence)),
            checked((ulong)Interlocked.Read(ref completedPolls)),
            checked((ulong)Interlocked.Read(ref timedOutPolls)),
            checked((ulong)Interlocked.Read(ref stalePolls)),
            checked((ulong)Interlocked.Read(ref missedOverlapPolls)),
            checked((ulong)Interlocked.Read(ref missedCapacityPolls)),
            checked((ulong)Interlocked.Read(ref admittedCuts)),
            checked((ulong)Interlocked.Read(ref processedObligations)),
            errorCode,
            scheduler.GetSnapshot());
    }

    private async Task<Result> HandleStartAsync(
        PollStartResult started,
        CancellationToken cancellationToken)
    {
        if (started.Status == PollStartStatus.MissedOverlap)
        {
            Interlocked.Increment(ref missedOverlapPolls);
            return Result.Success();
        }

        if (started.Status == PollStartStatus.MissedCapacity)
        {
            Interlocked.Increment(ref missedCapacityPolls);
            return Result.Success();
        }

        var attempt = started.Attempt!;
        var completion = scheduler.Complete(attempt, source.Poll(attempt));
        if (completion.IsFailure)
        {
            return Result.Failure(completion.Error!);
        }

        if (completion.Value.Status == PollCompletionStatus.TimedOut)
        {
            Interlocked.Increment(ref timedOutPolls);
            return Result.Success();
        }

        if (completion.Value.Status == PollCompletionStatus.Stale)
        {
            Interlocked.Increment(ref stalePolls);
            return Result.Success();
        }

        Interlocked.Increment(ref completedPolls);
        var enqueued = await enqueueCut(completion.Value.Cut!, cancellationToken).ConfigureAwait(false);
        if (enqueued.IsFailure)
        {
            return Result.Failure(enqueued.Error!);
        }

        Interlocked.Increment(ref admittedCuts);
        return await ProcessAvailableAsync().ConfigureAwait(false);
    }

    private async Task<Result> ProcessAvailableAsync()
    {
        for (var index = 0; index < maxProcessBatch; index++)
        {
            var processed = await processNext(CancellationToken.None).ConfigureAwait(false);
            if (processed.IsFailure)
            {
                return Result.Failure(processed.Error!);
            }

            if (!processed.Value)
            {
                return Result.Success();
            }

            Interlocked.Increment(ref processedObligations);
        }

        return Failure(
            "runtime.processing_batch_exhausted",
            "Runtime processing did not quiesce inside the configured bounded batch.");
    }

    private Result Fault(OperationError error)
    {
        lock (errorSync)
        {
            lastErrorCode = error.Code.Value;
        }

        Volatile.Write(ref state, (int)SimulatorPollingWorkerState.Faulted);
        return Result.Failure(error);
    }

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));
}
