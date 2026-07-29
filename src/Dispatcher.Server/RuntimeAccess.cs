using Dispatcher.Core;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;

namespace Dispatcher.Server;

public static class RuntimePermissions
{
    public static PermissionCode ReadCurrent { get; } = PermissionCode.From("runtime.current.read");

    public static PermissionCode ReadPoint(PointId pointId) =>
        PermissionCode.From($"runtime.point.p{pointId.Value:N}.read");
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
    private readonly CoreRuntimePublishedReader? publishedReader;
    private readonly IWallClock clock;

    public AuthorizedRuntimeReader(
        CoreRuntimePublishedReader publishedReader,
        IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(publishedReader);
        ArgumentNullException.ThrowIfNull(clock);
        this.publishedReader = publishedReader;
        this.clock = clock;
    }

    public AuthorizedRuntimeReader(IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
    }

    public async Task<Result<RuntimeReadinessPayload>> ReadReadinessAsync(
        SessionSnapshot? session,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        var authorization = Authorize(session);
        if (authorization.IsFailure)
        {
            return Result.Failure<RuntimeReadinessPayload>(authorization.Error!);
        }
        if (publishedReader is null)
        {
            return Unavailable<RuntimeReadinessPayload>();
        }

        try
        {
            var readiness = await publishedReader
                .ReadReadinessAsync(scopeId, cancellationToken)
                .ConfigureAwait(false);
            return Result.Success(new RuntimeReadinessPayload(
                scopeId.Value,
                readiness.Published,
                readiness.Ready,
                readiness.CanServeCurrent,
                readiness.MeasurementSemanticVersion,
                readiness.DegradationReasonCode,
                readiness.HeartbeatAt,
                readiness.PublishedAt));
        }
        catch (NpgsqlException)
        {
            return Unavailable<RuntimeReadinessPayload>();
        }
    }

    public async Task<Result<AuthorizedRuntimeSnapshot>> ReadSnapshotAsync(
        SessionSnapshot? session,
        RuntimeScopeId scopeId,
        IReadOnlySet<Guid>? requestedPointIds = null,
        CancellationToken cancellationToken = default)
    {
        var authorization = Authorize(session);
        if (authorization.IsFailure)
        {
            return Result.Failure<AuthorizedRuntimeSnapshot>(authorization.Error!);
        }
        if (publishedReader is null)
        {
            return Unavailable<AuthorizedRuntimeSnapshot>();
        }

        try
        {
            var snapshot = await publishedReader
                .ReadSnapshotAsync(scopeId, cancellationToken)
                .ConfigureAwait(false);
            var unavailable = ValidateReadiness<AuthorizedRuntimeSnapshot>(snapshot.Readiness);
            if (unavailable is not null)
            {
                return unavailable;
            }

            var permissions = authorization.Value.Session.Permissions;
            var authorizedPointIds = requestedPointIds is null
                ? null
                : requestedPointIds
                    .Where(pointId => pointId != Guid.Empty)
                    .Where(pointId => permissions.Allows(RuntimePermissions.ReadPoint(PointId.From(pointId))))
                    .ToHashSet();
            var points = Filter(snapshot.Entries, permissions, authorizedPointIds);
            return Result.Success(new AuthorizedRuntimeSnapshot(
                new RuntimeSnapshotPayload(scopeId.Value, 0, points),
                snapshot.Cursor.Value,
                authorizedPointIds ?? points.Select(point => point.PointId).ToHashSet()));
        }
        catch (PublishedCurrentReadLimitExceededException)
        {
            return Failure<AuthorizedRuntimeSnapshot>(
                "runtime.query_limit_exceeded",
                "Runtime snapshot exceeds the configured read capacity.");
        }
        catch (NpgsqlException)
        {
            return Unavailable<AuthorizedRuntimeSnapshot>();
        }
    }

    public async Task<Result<AuthorizedRuntimeDelta>> ReadDeltaAsync(
        SessionSnapshot? session,
        RuntimeScopeId scopeId,
        ulong coreCursor,
        IReadOnlySet<Guid>? requestedPointIds = null,
        CancellationToken cancellationToken = default)
    {
        var authorization = Authorize(session);
        if (authorization.IsFailure)
        {
            return Result.Failure<AuthorizedRuntimeDelta>(authorization.Error!);
        }
        if (publishedReader is null)
        {
            return Unavailable<AuthorizedRuntimeDelta>();
        }

        try
        {
            var delta = await publishedReader.ReadDeltaAsync(
                scopeId,
                new ConsumerCursor<PublishedCurrentEntry>(coreCursor),
                cancellationToken).ConfigureAwait(false);
            if (delta.Status == PublishedCurrentDeltaStatus.ScopeNotPublished)
            {
                return Failure<AuthorizedRuntimeDelta>(
                    "runtime.scope_not_found",
                    "Runtime scope was not published.");
            }
            var unavailable = ValidateReadiness<AuthorizedRuntimeDelta>(delta.Readiness);
            if (unavailable is not null)
            {
                return unavailable;
            }
            if (delta.Status is PublishedCurrentDeltaStatus.CursorTooOld or
                PublishedCurrentDeltaStatus.CursorAhead)
            {
                return Failure<AuthorizedRuntimeDelta>(
                    "runtime.cursor_gap",
                    "Runtime cursor cannot be resumed.");
            }

            return Result.Success(new AuthorizedRuntimeDelta(
                Filter(delta.Changes, authorization.Value.Session.Permissions, requestedPointIds),
                delta.To.Value));
        }
        catch (NpgsqlException)
        {
            return Unavailable<AuthorizedRuntimeDelta>();
        }
    }

    internal async Task<Result<CurrentSnapshot>> ReadEvidenceAsync(
        RuntimeScopeId scopeId,
        PointId pointId,
        CancellationToken cancellationToken = default)
    {
        if (publishedReader is null)
        {
            return Unavailable<CurrentSnapshot>();
        }

        try
        {
            var snapshot = await publishedReader
                .ReadSnapshotAsync(scopeId, cancellationToken)
                .ConfigureAwait(false);
            var unavailable = ValidateReadiness<CurrentSnapshot>(snapshot.Readiness);
            if (unavailable is not null)
            {
                return unavailable;
            }

            return Result.Success(new CurrentSnapshot(
                scopeId,
                new OwnerPosition<CurrentEntry>(snapshot.Cursor.Value),
                snapshot.Entries
                    .Where(entry => entry.PointId == pointId)
                    .Select(ToCurrentEntry)
                    .ToArray()));
        }
        catch (PublishedCurrentReadLimitExceededException)
        {
            return Failure<CurrentSnapshot>(
                "runtime.query_limit_exceeded",
                "Runtime snapshot exceeds the configured read capacity.");
        }
        catch (NpgsqlException)
        {
            return Unavailable<CurrentSnapshot>();
        }
    }

    private static RuntimePointPayload[] Filter(
        IEnumerable<PublishedCurrentEntry> entries,
        EffectivePermissions permissions,
        IReadOnlySet<Guid>? requestedPointIds) =>
        entries
            .Where(entry => requestedPointIds is null || requestedPointIds.Contains(entry.PointId.Value))
            .Where(entry => permissions.Allows(RuntimePermissions.ReadPoint(entry.PointId)))
            .Select(ToPayload)
            .ToArray();

    private static RuntimePointPayload ToPayload(PublishedCurrentEntry entry) => new(
        entry.PointId.Value,
        entry.Value.Value,
        entry.Unit.Symbol,
        entry.Quality.ToString(),
        entry.Freshness.ToString(),
        entry.SourceTimestamp.Value,
        entry.ReceiveTimestamp.Value,
        entry.ProcessedTimestamp.Value);

    private static CurrentEntry ToCurrentEntry(PublishedCurrentEntry entry) => new(
        entry.ScopeId,
        entry.SourceId,
        entry.PointId,
        entry.BindingGeneration,
        entry.SessionGeneration,
        entry.SourcePosition,
        new OwnerPosition<CurrentEntry>(entry.CurrentPosition.Value),
        entry.Value,
        entry.Unit,
        entry.Quality,
        entry.Freshness,
        entry.SourceTimestamp,
        entry.ReceiveTimestamp,
        entry.ProcessedTimestamp,
        new MonotonicTimestamp(0));

    private Result<AuthorizedAccess> Authorize(SessionSnapshot? session) =>
        SessionAuthorization.AuthorizeAccess(session, RuntimePermissions.ReadCurrent, clock);

    private static Result<TValue>? ValidateReadiness<TValue>(
        PublishedRuntimeReadiness readiness)
    {
        if (!readiness.Published)
        {
            return Failure<TValue>(
                "runtime.scope_not_found",
                "Runtime scope was not published.");
        }
        return readiness.CanServeCurrent
            ? null
            : Failure<TValue>(
                "runtime.scope_not_ready",
                "Runtime scope is not ready to serve current values.");
    }

    private static Result<TValue> Unavailable<TValue>() =>
        Failure<TValue>(
            "runtime.current_unavailable",
            "Runtime current is unavailable.");

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));
}
