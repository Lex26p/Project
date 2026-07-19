using Dispatcher.Semantics;

namespace Dispatcher.Core;

public sealed class CoreRuntime
{
    private readonly object sync = new();
    private readonly RuntimeScopeId scopeId;
    private readonly IWallClock wallClock;
    private readonly IMonotonicClock monotonicClock;
    private readonly Dictionary<SourceId, ulong> sourcePositions = [];
    private readonly Dictionary<PointId, CurrentEntry> current = [];
    private readonly List<CurrentEntry> changes = [];
    private ulong currentPosition;

    public CoreRuntime(RuntimeScopeId scopeId, IWallClock wallClock, IMonotonicClock monotonicClock)
    {
        _ = scopeId.Value;
        ArgumentNullException.ThrowIfNull(wallClock);
        ArgumentNullException.ThrowIfNull(monotonicClock);
        this.scopeId = scopeId;
        this.wallClock = wallClock;
        this.monotonicClock = monotonicClock;
    }

    public Result<CurrentEntry> Admit(SourceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (sync)
        {
            if (observation.ScopeId != scopeId)
            {
                return Failure("core.scope_mismatch", "Observation belongs to another runtime scope.");
            }

            sourcePositions.TryGetValue(observation.SourceId, out var lastSourcePosition);
            if (observation.SourcePosition.Value != checked(lastSourcePosition + 1))
            {
                return Failure("core.source_position", "Observation source position is not the next accepted position.");
            }

            var receivedAt = ReceiveTimestamp.FromUtc(wallClock.GetUtcNow());
            var processedMonotonic = monotonicClock.GetTimestamp();
            var processedAt = ProcessedTimestamp.FromUtc(wallClock.GetUtcNow());
            var nextCurrentPosition = checked(currentPosition + 1);
            var entry = new CurrentEntry(
                scopeId,
                observation.SourceId,
                observation.PointId,
                observation.SourcePosition,
                new OwnerPosition<CurrentEntry>(nextCurrentPosition),
                observation.Value,
                observation.Unit,
                observation.Quality,
                observation.Freshness,
                observation.SourceTimestamp,
                receivedAt,
                processedAt,
                processedMonotonic);

            sourcePositions[observation.SourceId] = observation.SourcePosition.Value;
            current[observation.PointId] = entry;
            changes.Add(entry);
            currentPosition = nextCurrentPosition;
            return Result.Success(entry);
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

    private static Result<CurrentEntry> Failure(string code, string message) =>
        Result.Failure<CurrentEntry>(new OperationError(ErrorCode.From(code), message));
}
