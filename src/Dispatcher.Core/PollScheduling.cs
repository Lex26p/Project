using Dispatcher.Semantics;

namespace Dispatcher.Core;

public sealed record PollScheduleLimits
{
    public PollScheduleLimits(TimeSpan timeout, int maxBindings, int maxInFlight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBindings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInFlight);
        Timeout = timeout;
        MaxBindings = maxBindings;
        MaxInFlight = maxInFlight;
    }

    public TimeSpan Timeout { get; }

    public int MaxBindings { get; }

    public int MaxInFlight { get; }
}

public sealed record PollAttemptToken(
    SourceBinding Binding,
    ulong ScheduleSequence,
    MonotonicTimestamp StartedAt);

public enum PollStartStatus
{
    Started = 1,
    MissedOverlap = 2,
    MissedCapacity = 3,
}

public sealed record PollStartResult(
    PollStartStatus Status,
    ulong ScheduleSequence,
    PollAttemptToken? Attempt);

public enum PollCompletionStatus
{
    Completed = 1,
    TimedOut = 2,
    Stale = 3,
}

public sealed record PollCompletion(PollCompletionStatus Status, RuntimeCut? Cut);

public sealed record PollSchedulerSnapshot(
    int BindingCount,
    int InFlightCount,
    ulong MissedOverlapCount,
    ulong MissedCapacityCount);

public sealed class BoundedPollScheduler
{
    private readonly object sync = new();
    private readonly RuntimeScopeId scopeId;
    private readonly PollScheduleLimits limits;
    private readonly IMonotonicClock clock;
    private readonly Dictionary<SourceId, BindingState> bindings = [];
    private readonly Dictionary<SourceId, PollAttemptToken> inFlight = [];
    private ulong missedOverlapCount;
    private ulong missedCapacityCount;

    public BoundedPollScheduler(
        RuntimeScopeId scopeId,
        PollScheduleLimits limits,
        IMonotonicClock clock)
    {
        _ = scopeId.Value;
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(clock);
        this.scopeId = scopeId;
        this.limits = limits;
        this.clock = clock;
    }

    public Result ActivateBinding(SourceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (sync)
        {
            if (binding.ScopeId != scopeId)
            {
                return Failure("poll.scope_mismatch", "Source binding belongs to another scheduler scope.");
            }

            if (bindings.TryGetValue(binding.SourceId, out var state))
            {
                if (state.Binding == binding)
                {
                    return Result.Success();
                }

                if (!binding.IsNewerThan(state.Binding))
                {
                    return Failure("poll.binding_stale", "Source binding generation is stale.");
                }

                inFlight.Remove(binding.SourceId);
                bindings[binding.SourceId] = new BindingState(binding, 0);
                return Result.Success();
            }

            if (bindings.Count >= limits.MaxBindings)
            {
                return Failure("poll.binding_capacity", "The configured source binding capacity is exhausted.");
            }

            bindings.Add(binding.SourceId, new BindingState(binding, 0));
            return Result.Success();
        }
    }

    public Result<PollStartResult> TryStart(SourceBinding binding, ulong scheduleSequence)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentOutOfRangeException.ThrowIfZero(scheduleSequence);

        lock (sync)
        {
            if (!bindings.TryGetValue(binding.SourceId, out var state) || state.Binding != binding)
            {
                return Failure<PollStartResult>("poll.binding_stale", "Only the active source binding can start a poll.");
            }

            if (scheduleSequence != checked(state.LastScheduleSequence + 1))
            {
                return Failure<PollStartResult>(
                    "poll.schedule_sequence",
                    "Scheduled poll sequence must be the next sequence for this source session.");
            }

            bindings[binding.SourceId] = state with { LastScheduleSequence = scheduleSequence };
            if (inFlight.ContainsKey(binding.SourceId))
            {
                missedOverlapCount = checked(missedOverlapCount + 1);
                return Result.Success(new PollStartResult(PollStartStatus.MissedOverlap, scheduleSequence, null));
            }

            if (inFlight.Count >= limits.MaxInFlight)
            {
                missedCapacityCount = checked(missedCapacityCount + 1);
                return Result.Success(new PollStartResult(PollStartStatus.MissedCapacity, scheduleSequence, null));
            }

            var attempt = new PollAttemptToken(binding, scheduleSequence, clock.GetTimestamp());
            inFlight.Add(binding.SourceId, attempt);
            return Result.Success(new PollStartResult(PollStartStatus.Started, scheduleSequence, attempt));
        }
    }

    public Result<PollCompletion> Complete(
        PollAttemptToken attempt,
        IEnumerable<SourceObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(observations);
        lock (sync)
        {
            if (!bindings.TryGetValue(attempt.Binding.SourceId, out var state) ||
                state.Binding != attempt.Binding ||
                !inFlight.TryGetValue(attempt.Binding.SourceId, out var current) ||
                current != attempt)
            {
                return Result.Success(new PollCompletion(PollCompletionStatus.Stale, null));
            }

            inFlight.Remove(attempt.Binding.SourceId);
            var now = clock.GetTimestamp();
            if (clock.GetElapsedTime(attempt.StartedAt, now) >= limits.Timeout)
            {
                return Result.Success(new PollCompletion(PollCompletionStatus.TimedOut, null));
            }

            var cut = RuntimeCut.Normalize(attempt.Binding, attempt.ScheduleSequence, observations);
            return cut.IsSuccess
                ? Result.Success(new PollCompletion(PollCompletionStatus.Completed, cut.Value))
                : Result.Failure<PollCompletion>(cut.Error!);
        }
    }

    public IReadOnlyList<PollAttemptToken> ExpireTimedOut()
    {
        lock (sync)
        {
            var now = clock.GetTimestamp();
            var expired = inFlight.Values
                .Where(attempt => clock.GetElapsedTime(attempt.StartedAt, now) >= limits.Timeout)
                .OrderBy(attempt => attempt.Binding.SourceId.Value)
                .ToArray();
            foreach (var attempt in expired)
            {
                inFlight.Remove(attempt.Binding.SourceId);
            }

            return expired;
        }
    }

    public PollSchedulerSnapshot GetSnapshot()
    {
        lock (sync)
        {
            return new PollSchedulerSnapshot(
                bindings.Count,
                inFlight.Count,
                missedOverlapCount,
                missedCapacityCount);
        }
    }

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));

    private sealed record BindingState(SourceBinding Binding, ulong LastScheduleSequence);
}
