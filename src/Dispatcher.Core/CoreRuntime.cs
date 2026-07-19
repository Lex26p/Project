using Dispatcher.Semantics;

namespace Dispatcher.Core;

public sealed class CoreRuntime
{
    private readonly object sync = new();
    private readonly RuntimeScopeId scopeId;
    private readonly IWallClock wallClock;
    private readonly IMonotonicClock monotonicClock;
    private readonly Dictionary<SourceId, SourceBinding> bindings = [];
    private readonly Dictionary<SourceId, ulong> sourcePositions = [];
    private readonly Dictionary<PointId, CurrentEntry> current = [];
    private readonly Dictionary<SourceId, SourceLiveness> liveness = [];
    private readonly List<CurrentEntry> changes = [];
    private ulong currentPosition;
    private ulong livenessPosition;

    public CoreRuntime(RuntimeScopeId scopeId, IWallClock wallClock, IMonotonicClock monotonicClock)
    {
        _ = scopeId.Value;
        ArgumentNullException.ThrowIfNull(wallClock);
        ArgumentNullException.ThrowIfNull(monotonicClock);
        this.scopeId = scopeId;
        this.wallClock = wallClock;
        this.monotonicClock = monotonicClock;
    }

    public Result ActivateBinding(SourceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (sync)
        {
            if (binding.ScopeId != scopeId)
            {
                return Failure("core.scope_mismatch", "Source binding belongs to another runtime scope.");
            }

            if (bindings.TryGetValue(binding.SourceId, out var active))
            {
                if (active == binding)
                {
                    return Result.Success();
                }

                if (!binding.IsNewerThan(active))
                {
                    return Failure("core.binding_stale", "Source binding generation is stale.");
                }
            }

            bindings[binding.SourceId] = binding;
            sourcePositions[binding.SourceId] = 0;
            liveness.Remove(binding.SourceId);
            return Result.Success();
        }
    }

    public Result<RuntimeCutAcceptance> Apply(RuntimeCut cut)
    {
        ArgumentNullException.ThrowIfNull(cut);
        lock (sync)
        {
            if (cut.Binding.ScopeId != scopeId)
            {
                return Failure<RuntimeCutAcceptance>("core.scope_mismatch", "RuntimeCut belongs to another runtime scope.");
            }

            if (!bindings.TryGetValue(cut.Binding.SourceId, out var active) || active != cut.Binding)
            {
                return Failure<RuntimeCutAcceptance>("core.binding_stale", "RuntimeCut source binding is not active.");
            }

            sourcePositions.TryGetValue(cut.Binding.SourceId, out var lastSourcePosition);
            var expectedPosition = lastSourcePosition;
            foreach (var observation in cut.Observations.OrderBy(item => item.SourcePosition.Value))
            {
                expectedPosition = checked(expectedPosition + 1);
                if (observation.SourcePosition.Value != expectedPosition)
                {
                    return Failure<RuntimeCutAcceptance>(
                        "core.source_position",
                        "RuntimeCut source positions are not the next contiguous positions.");
                }
            }

            var receivedAt = ReceiveTimestamp.FromUtc(wallClock.GetUtcNow());
            var processedMonotonic = monotonicClock.GetTimestamp();
            var processedAt = ProcessedTimestamp.FromUtc(wallClock.GetUtcNow());
            var nextCurrentPosition = currentPosition;
            var transitions = new List<CurrentEntry>(cut.Observations.Count);
            foreach (var observation in cut.Observations)
            {
                if (current.TryGetValue(observation.PointId, out var existing) &&
                    IsSameCurrent(existing, cut.Binding, observation))
                {
                    continue;
                }

                nextCurrentPosition = checked(nextCurrentPosition + 1);
                transitions.Add(new CurrentEntry(
                    scopeId,
                    observation.SourceId,
                    observation.PointId,
                    cut.Binding.BindingGeneration,
                    cut.Binding.SessionGeneration,
                    observation.SourcePosition,
                    new OwnerPosition<CurrentEntry>(nextCurrentPosition),
                    observation.Value,
                    observation.Unit,
                    observation.Quality,
                    observation.Freshness,
                    observation.SourceTimestamp,
                    receivedAt,
                    processedAt,
                    processedMonotonic));
            }

            var nextLivenessPosition = checked(livenessPosition + 1);
            var sourceLiveness = new SourceLiveness(
                scopeId,
                cut.Binding.SourceId,
                cut.Binding.BindingGeneration,
                cut.Binding.SessionGeneration,
                cut.ScheduleSequence,
                expectedPosition,
                cut.Observations.Count,
                new OwnerPosition<SourceLiveness>(nextLivenessPosition),
                receivedAt,
                processedAt,
                processedMonotonic);
            sourcePositions[cut.Binding.SourceId] = expectedPosition;
            foreach (var transition in transitions)
            {
                current[transition.PointId] = transition;
                changes.Add(transition);
            }

            currentPosition = nextCurrentPosition;
            liveness[cut.Binding.SourceId] = sourceLiveness;
            livenessPosition = nextLivenessPosition;
            return Result.Success(new RuntimeCutAcceptance(cut, transitions, sourceLiveness));
        }
    }

    public CurrentSnapshot GetSnapshot()
    {
        lock (sync)
        {
            var entries = current.Values
                .OrderBy(entry => entry.PointId.Value)
                .ToArray();
            return new CurrentSnapshot(scopeId, new OwnerPosition<CurrentEntry>(currentPosition), entries);
        }
    }

    public CurrentDelta GetDelta(ConsumerCursor<CurrentEntry> cursor)
    {
        lock (sync)
        {
            if (cursor.Value > currentPosition)
            {
                throw new ArgumentOutOfRangeException(nameof(cursor), "Cursor is ahead of current.");
            }

            var delta = changes
                .Where(entry => entry.CurrentPosition.Value > cursor.Value)
                .ToArray();
            return new CurrentDelta(
                scopeId,
                cursor,
                new ConsumerCursor<CurrentEntry>(currentPosition),
                delta);
        }
    }

    public SourceLivenessSnapshot GetLivenessSnapshot()
    {
        lock (sync)
        {
            return new SourceLivenessSnapshot(
                scopeId,
                new OwnerPosition<SourceLiveness>(livenessPosition),
                liveness.Values.OrderBy(item => item.SourceId.Value).ToArray());
        }
    }

    private static bool IsSameCurrent(
        CurrentEntry existing,
        SourceBinding binding,
        SourceObservation observation) =>
        existing.SourceId == observation.SourceId &&
        existing.BindingGeneration == binding.BindingGeneration &&
        existing.SessionGeneration == binding.SessionGeneration &&
        existing.Value.Value == observation.Value.Value &&
        existing.Unit == observation.Unit &&
        existing.Quality == observation.Quality &&
        existing.Freshness == observation.Freshness;

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));
}
