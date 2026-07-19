using System.Collections.Concurrent;
using Dispatcher.Core;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Server;

public static class RuntimePermissions
{
    public static PermissionCode ReadCurrent { get; } = PermissionCode.From("runtime.current.read");

    public static PermissionCode ReadPoint(PointId pointId) =>
        PermissionCode.From($"runtime.point.p{pointId.Value:N}.read");
}

public sealed class RuntimeRegistry
{
    private readonly ConcurrentDictionary<RuntimeScopeId, CoreRuntime> runtimes = new();

    public void Add(RuntimeScopeId scopeId, CoreRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!runtimes.TryAdd(scopeId, runtime))
        {
            throw new InvalidOperationException("The runtime scope is already registered.");
        }
    }

    public bool TryGet(RuntimeScopeId scopeId, out CoreRuntime? runtime) =>
        runtimes.TryGetValue(scopeId, out runtime);
}

public sealed record AuthorizedRuntimeSnapshot(
    RuntimeSnapshotPayload Payload,
    ulong CoreCursor,
    IReadOnlySet<Guid> PointIds);

public sealed record AuthorizedRuntimeDelta(
    IReadOnlyList<RuntimePointPayload> Changes,
    ulong CoreCursor);

public sealed class AuthorizedRuntimeReader
{
    private readonly RuntimeRegistry registry;
    private readonly IWallClock clock;

    public AuthorizedRuntimeReader(RuntimeRegistry registry, IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clock);
        this.registry = registry;
        this.clock = clock;
    }

    public Result<AuthorizedRuntimeSnapshot> ReadSnapshot(SessionSnapshot? session, RuntimeScopeId scopeId)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(session, RuntimePermissions.ReadCurrent, clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<AuthorizedRuntimeSnapshot>(authorization.Error!);
        }

        if (!registry.TryGet(scopeId, out var runtime))
        {
            return Failure<AuthorizedRuntimeSnapshot>("runtime.scope_not_found", "Runtime scope was not found.");
        }

        var snapshot = runtime!.GetSnapshot();
        var points = Filter(snapshot.Entries, authorization.Value.Session.Permissions);
        return Result.Success(new AuthorizedRuntimeSnapshot(
            new RuntimeSnapshotPayload(scopeId.Value, 0, points),
            snapshot.Position.Value,
            points.Select(point => point.PointId).ToHashSet()));
    }

    public Result<AuthorizedRuntimeDelta> ReadDelta(
        SessionSnapshot? session,
        RuntimeScopeId scopeId,
        ulong coreCursor)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(session, RuntimePermissions.ReadCurrent, clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<AuthorizedRuntimeDelta>(authorization.Error!);
        }

        if (!registry.TryGet(scopeId, out var runtime))
        {
            return Failure<AuthorizedRuntimeDelta>("runtime.scope_not_found", "Runtime scope was not found.");
        }

        CurrentDelta delta;
        try
        {
            delta = runtime!.GetDelta(new ConsumerCursor<CurrentEntry>(coreCursor));
        }
        catch (ArgumentOutOfRangeException)
        {
            return Failure<AuthorizedRuntimeDelta>("runtime.cursor_gap", "Runtime cursor cannot be resumed.");
        }

        return Result.Success(new AuthorizedRuntimeDelta(
            Filter(delta.Changes, authorization.Value.Session.Permissions),
            delta.To.Value));
    }

    private static RuntimePointPayload[] Filter(
        IEnumerable<CurrentEntry> entries,
        EffectivePermissions permissions) =>
        entries
            .Where(entry => permissions.Allows(RuntimePermissions.ReadPoint(entry.PointId)))
            .Select(ToPayload)
            .ToArray();

    private static RuntimePointPayload ToPayload(CurrentEntry entry) => new(
        entry.PointId.Value,
        entry.Value.Value,
        entry.Unit.Symbol,
        entry.Quality.ToString(),
        entry.Freshness.ToString());

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));
}
