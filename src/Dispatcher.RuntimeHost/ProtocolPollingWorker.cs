using System.Collections.Concurrent;
using Dispatcher.Core;
using Dispatcher.Protocols;
using Dispatcher.Semantics;

namespace Dispatcher.RuntimeHost;

public enum ProtocolPollingWorkerState
{
    Created = 1,
    Running = 2,
    Stopped = 3,
    Faulted = 4,
}

public enum ProtocolSourceReadiness
{
    Ready = 1,
    Degraded = 2,
}

public sealed record ProtocolPollingSourceSnapshot(
    SourceId SourceId,
    SourceBinding Binding,
    ProtocolSourceReadiness Readiness,
    ulong ScheduleSequence,
    ulong SuccessfulPolls,
    ulong FailedPolls,
    ulong TimedOutPolls,
    ulong StalePolls,
    string? LastReasonCode);

public sealed record ProtocolPollingWorkerSnapshot(
    ProtocolPollingWorkerState State,
    IReadOnlyList<ProtocolPollingSourceSnapshot> Sources,
    ulong MissedOverlapPolls,
    ulong MissedCapacityPolls,
    ulong AdmittedCuts,
    ulong ProcessedObligations,
    string? LastErrorCode,
    PollSchedulerSnapshot Scheduler);

public sealed class ProtocolPollingWorker
{
    private readonly IReadOnlyList<IRuntimeProtocolSource> sources;
    private readonly BoundedPollScheduler scheduler;
    private readonly TimeSpan pollInterval;
    private readonly int maxProcessBatch;
    private readonly RuntimeProcess process;
    private readonly RuntimeWorkerDelay delay;
    private readonly ConcurrentDictionary<SourceId, SourceCounters> counters = [];
    private int runClaimed;
    private int state = (int)ProtocolPollingWorkerState.Created;
    private long missedOverlapPolls;
    private long missedCapacityPolls;
    private long admittedCuts;
    private long processedObligations;
    private string? lastErrorCode;

    internal ProtocolPollingWorker(
        IReadOnlyList<IRuntimeProtocolSource> sources,
        BoundedPollScheduler scheduler,
        TimeSpan pollInterval,
        int maxProcessBatch,
        RuntimeProcess process,
        RuntimeWorkerDelay? delay = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxProcessBatch);
        ArgumentNullException.ThrowIfNull(process);
        this.sources = sources;
        this.scheduler = scheduler;
        this.pollInterval = pollInterval;
        this.maxProcessBatch = maxProcessBatch;
        this.process = process;
        this.delay = delay ?? Task.Delay;
        foreach (var source in sources)
        {
            counters[source.Binding.SourceId] = new SourceCounters(source.Binding);
        }
    }

    public async Task<Result> RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref runClaimed, 1, 0) != 0)
        {
            return Failure(
                "runtime.protocol_worker_state",
                "Protocol polling worker can only run once.");
        }

        try
        {
            foreach (var source in sources)
            {
                var scheduled = scheduler.ActivateBinding(source.Binding);
                if (scheduled.IsFailure)
                {
                    return Fault(scheduled.Error!);
                }

                var activated = process.ActivateBinding(source.Binding);
                if (activated.IsFailure)
                {
                    return Fault(activated.Error!);
                }
            }

            Volatile.Write(ref state, (int)ProtocolPollingWorkerState.Running);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var polls = new List<Task<Result>>(sources.Count);
                foreach (var source in sources)
                {
                    var sourceCounters = counters[source.Binding.SourceId];
                    var sequence = sourceCounters.NextSequence();
                    var started = scheduler.TryStart(source.Binding, sequence);
                    if (started.IsFailure)
                    {
                        return Fault(started.Error!);
                    }

                    if (started.Value.Status == PollStartStatus.MissedOverlap)
                    {
                        Interlocked.Increment(ref missedOverlapPolls);
                    }
                    else if (started.Value.Status == PollStartStatus.MissedCapacity)
                    {
                        Interlocked.Increment(ref missedCapacityPolls);
                    }
                    else
                    {
                        polls.Add(PollAsync(
                            source,
                            sourceCounters,
                            started.Value.Attempt!,
                            cancellationToken));
                    }
                }

                var results = await Task.WhenAll(polls).ConfigureAwait(false);
                var failed = results.FirstOrDefault(result => result.IsFailure);
                if (failed is not null)
                {
                    return Fault(failed.Error!);
                }

                var processed = await ProcessAvailableAsync().ConfigureAwait(false);
                if (processed.IsFailure)
                {
                    return Fault(processed.Error!);
                }

                await delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref state, (int)ProtocolPollingWorkerState.Stopped);
            return Result.Success();
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return Fault(new OperationError(
                ErrorCode.From("runtime.protocol_poll_failed"),
                "Protocol polling failed without exposing configuration or runtime data."));
        }
    }

    public ProtocolPollingWorkerSnapshot GetSnapshot() =>
        new(
            (ProtocolPollingWorkerState)Volatile.Read(ref state),
            counters.Values
                .OrderBy(item => item.Binding.SourceId.Value)
                .Select(item => item.Snapshot())
                .ToArray(),
            checked((ulong)Interlocked.Read(ref missedOverlapPolls)),
            checked((ulong)Interlocked.Read(ref missedCapacityPolls)),
            checked((ulong)Interlocked.Read(ref admittedCuts)),
            checked((ulong)Interlocked.Read(ref processedObligations)),
            Volatile.Read(ref lastErrorCode),
            scheduler.GetSnapshot());

    private async Task<Result> PollAsync(
        IRuntimeProtocolSource source,
        SourceCounters sourceCounters,
        PollAttemptToken attempt,
        CancellationToken cancellationToken)
    {
        var acquired = await process.AcquireCutAsync(
            new ProtocolSourceRequest(
                source.Binding,
                attempt.ScheduleSequence,
                source.SecretReference),
            cancellationToken).ConfigureAwait(false);
        RuntimeCut cut;
        if (acquired.IsFailure)
        {
            var status = scheduler.Abandon(attempt);
            if (status == PollCompletionStatus.Stale ||
                acquired.Error?.Code.Value == "protocol.binding_stale")
            {
                sourceCounters.RecordStale();
                return Result.Success();
            }

            sourceCounters.RecordFailure(
                acquired.Error?.Code.Value ?? "protocol.io_failed",
                status == PollCompletionStatus.TimedOut);
            var unavailable = source.CreateUnavailableCut(
                attempt.ScheduleSequence);
            if (unavailable.IsFailure)
            {
                return Result.Failure(unavailable.Error!);
            }

            cut = unavailable.Value;
        }
        else
        {
            var completion = scheduler.Complete(
                attempt,
                acquired.Value.Observations);
            if (completion.IsFailure)
            {
                return Result.Failure(completion.Error!);
            }

            if (completion.Value.Status == PollCompletionStatus.Stale)
            {
                sourceCounters.RecordStale();
                return Result.Success();
            }

            if (completion.Value.Status == PollCompletionStatus.TimedOut)
            {
                sourceCounters.RecordFailure("protocol.io_timeout", timedOut: true);
                var unavailable = source.CreateUnavailableCut(
                    attempt.ScheduleSequence);
                if (unavailable.IsFailure)
                {
                    return Result.Failure(unavailable.Error!);
                }

                cut = unavailable.Value;
            }
            else
            {
                sourceCounters.RecordSuccess();
                cut = completion.Value.Cut!;
            }
        }

        var enqueued = await process.EnqueueAsync(cut, cancellationToken).ConfigureAwait(false);
        if (enqueued.IsFailure)
        {
            return Result.Failure(enqueued.Error!);
        }

        Interlocked.Increment(ref admittedCuts);
        return Result.Success();
    }

    private async Task<Result> ProcessAvailableAsync()
    {
        for (var index = 0; index < maxProcessBatch; index++)
        {
            var processed = await process.ProcessNextAsync(CancellationToken.None).ConfigureAwait(false);
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
        Volatile.Write(ref lastErrorCode, error.Code.Value);
        Volatile.Write(ref state, (int)ProtocolPollingWorkerState.Faulted);
        return Result.Failure(error);
    }

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private sealed class SourceCounters
    {
        private long scheduleSequence;
        private long successfulPolls;
        private long failedPolls;
        private long timedOutPolls;
        private long stalePolls;
        private string? lastReasonCode;
        private int readiness = (int)ProtocolSourceReadiness.Degraded;

        public SourceCounters(SourceBinding binding) => Binding = binding;

        public SourceBinding Binding { get; }

        public ulong NextSequence() =>
            checked((ulong)Interlocked.Increment(ref scheduleSequence));

        public void RecordSuccess()
        {
            Interlocked.Increment(ref successfulPolls);
            Volatile.Write(ref lastReasonCode, "protocol.ok");
            Volatile.Write(ref readiness, (int)ProtocolSourceReadiness.Ready);
        }

        public void RecordFailure(string reasonCode, bool timedOut)
        {
            Interlocked.Increment(ref failedPolls);
            if (timedOut)
            {
                Interlocked.Increment(ref timedOutPolls);
            }

            Volatile.Write(ref lastReasonCode, reasonCode);
            Volatile.Write(ref readiness, (int)ProtocolSourceReadiness.Degraded);
        }

        public void RecordStale()
        {
            Interlocked.Increment(ref stalePolls);
            Volatile.Write(ref lastReasonCode, "protocol.binding_stale");
        }

        public ProtocolPollingSourceSnapshot Snapshot() =>
            new(
                Binding.SourceId,
                Binding,
                (ProtocolSourceReadiness)Volatile.Read(ref readiness),
                checked((ulong)Interlocked.Read(ref scheduleSequence)),
                checked((ulong)Interlocked.Read(ref successfulPolls)),
                checked((ulong)Interlocked.Read(ref failedPolls)),
                checked((ulong)Interlocked.Read(ref timedOutPolls)),
                checked((ulong)Interlocked.Read(ref stalePolls)),
                Volatile.Read(ref lastReasonCode));
    }
}
