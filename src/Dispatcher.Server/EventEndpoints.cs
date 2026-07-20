using System.Collections.Concurrent;
using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Dispatcher.Server;

public static class EventPermissions
{
    public static PermissionCode ReadDispatcher { get; } = PermissionCode.From("events.dispatcher.read");
}

public sealed record AuthorizedOccurrenceSnapshot(
    OccurrenceProjectionSnapshot Snapshot,
    IReadOnlySet<PointId> PointIds);

public sealed class AuthorizedEventDispatcher
{
    private readonly EventStore store;
    private readonly IWallClock clock;

    public AuthorizedEventDispatcher(EventStore store, IWallClock clock)
    {
        this.store = store;
        this.clock = clock;
    }

    public async Task<Result<EventQueryPage>> QueryAsync(
        SessionSnapshot? session,
        EventQueryRequest request,
        CancellationToken cancellationToken)
    {
        var points = await AuthorizePointsAsync(session, request.ScopeId, request.PointIds, cancellationToken)
            .ConfigureAwait(false);
        return points.IsSuccess
            ? await store.QueryAsync(request with { PointIds = points.Value }, cancellationToken).ConfigureAwait(false)
            : Result.Failure<EventQueryPage>(points.Error!);
    }

    public async Task<Result<EventDispatcherCounters>> CountAsync(
        SessionSnapshot? session,
        EventQueryRequest request,
        CancellationToken cancellationToken)
    {
        var points = await AuthorizePointsAsync(session, request.ScopeId, request.PointIds, cancellationToken)
            .ConfigureAwait(false);
        return points.IsSuccess
            ? Result.Success(await store.CountAsync(
                request with { PointIds = points.Value },
                cancellationToken).ConfigureAwait(false))
            : Result.Failure<EventDispatcherCounters>(points.Error!);
    }

    public async Task<Result<AuthorizedOccurrenceSnapshot>> ReadOccurrenceSnapshotAsync(
        SessionSnapshot? session,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        var points = await AuthorizePointsAsync(session, scopeId, null, cancellationToken).ConfigureAwait(false);
        if (points.IsFailure)
        {
            return Result.Failure<AuthorizedOccurrenceSnapshot>(points.Error!);
        }

        var snapshot = await store.ReadProjectionSnapshotAsync(scopeId, points.Value, cancellationToken)
            .ConfigureAwait(false);
        return Result.Success(new AuthorizedOccurrenceSnapshot(snapshot, points.Value));
    }

    public async Task<Result<OccurrenceFeedPage>> ReadOccurrenceFeedAsync(
        SessionSnapshot? session,
        RuntimeScopeId scopeId,
        ulong cursor,
        IReadOnlySet<PointId> subscribedPoints,
        CancellationToken cancellationToken)
    {
        var points = await AuthorizePointsAsync(session, scopeId, subscribedPoints, cancellationToken)
            .ConfigureAwait(false);
        if (points.IsFailure || !points.Value.SetEquals(subscribedPoints))
        {
            return Result.Failure<OccurrenceFeedPage>(points.Error ?? new OperationError(
                ErrorCode.From("permission.invalidated"),
                "Event Dispatcher point permissions changed."));
        }

        return Result.Success(await store.ReadProjectionFeedAsync(
            scopeId,
            cursor,
            points.Value,
            cancellationToken).ConfigureAwait(false));
    }

    private async Task<Result<IReadOnlySet<PointId>>> AuthorizePointsAsync(
        SessionSnapshot? session,
        RuntimeScopeId scopeId,
        IReadOnlySet<PointId>? requestedPoints,
        CancellationToken cancellationToken)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(session, EventPermissions.ReadDispatcher, clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<IReadOnlySet<PointId>>(authorization.Error!);
        }

        var known = await store.ReadKnownPointIdsAsync(scopeId, cancellationToken).ConfigureAwait(false);
        var requested = requestedPoints is null || requestedPoints.Count == 0 ? known : requestedPoints;
        IReadOnlySet<PointId> allowed = requested
            .Where(known.Contains)
            .Where(pointId => authorization.Value.Session.Permissions.Allows(RuntimePermissions.ReadPoint(pointId)))
            .ToHashSet();
        return Result.Success(allowed);
    }
}

internal sealed record EventRealtimeSubscription(
    SessionId SessionId,
    RuntimeScopeId ScopeId,
    ulong Cursor,
    IReadOnlySet<PointId> PointIds);

public sealed class EventRealtimeSubscriptionStore
{
    private readonly ConcurrentDictionary<string, EventRealtimeSubscription> subscriptions = new();

    internal void Set(string connectionId, EventRealtimeSubscription subscription) =>
        subscriptions[connectionId] = subscription;

    internal bool TryGet(string connectionId, out EventRealtimeSubscription? subscription) =>
        subscriptions.TryGetValue(connectionId, out subscription);

    internal void Remove(string connectionId) => subscriptions.TryRemove(connectionId, out _);
}

public sealed class EventRealtimeHub : Hub
{
    private readonly RequestSessionResolver sessions;
    private readonly AuthorizedEventDispatcher dispatcher;
    private readonly EventRealtimeSubscriptionStore subscriptions;

    public EventRealtimeHub(
        RequestSessionResolver sessions,
        AuthorizedEventDispatcher dispatcher,
        EventRealtimeSubscriptionStore subscriptions)
    {
        this.sessions = sessions;
        this.dispatcher = dispatcher;
        this.subscriptions = subscriptions;
    }

    public async Task<OccurrenceSnapshotPayload> Bootstrap(Guid scopeId)
    {
        var session = sessions.Resolve(Context.GetHttpContext());
        var result = await dispatcher.ReadOccurrenceSnapshotAsync(
            session,
            RuntimeScopeId.From(scopeId),
            Context.ConnectionAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new HubException(result.Error!.Code.Value);
        }

        subscriptions.Set(Context.ConnectionId, new EventRealtimeSubscription(
            session!.Id,
            RuntimeScopeId.From(scopeId),
            result.Value.Snapshot.Cursor,
            result.Value.PointIds));
        return ToPayload(result.Value.Snapshot);
    }

    public async Task<OccurrenceFeedPayload> Poll(Guid scopeId, ulong cursor)
    {
        if (!subscriptions.TryGet(Context.ConnectionId, out var subscription) ||
            subscription!.ScopeId != RuntimeScopeId.From(scopeId) || subscription.Cursor != cursor)
        {
            subscriptions.Remove(Context.ConnectionId);
            return new OccurrenceFeedPayload(OccurrenceFeedKind.Gap.ToString(), cursor, cursor, []);
        }

        var session = sessions.Resolve(Context.GetHttpContext());
        if (session is null || session.Id != subscription.SessionId)
        {
            subscriptions.Remove(Context.ConnectionId);
            return new OccurrenceFeedPayload("PermissionInvalidated", cursor, cursor, []);
        }

        var result = await dispatcher.ReadOccurrenceFeedAsync(
            session,
            subscription.ScopeId,
            cursor,
            subscription.PointIds,
            Context.ConnectionAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            subscriptions.Remove(Context.ConnectionId);
            return new OccurrenceFeedPayload("PermissionInvalidated", cursor, cursor, []);
        }

        var page = result.Value;
        if (page.Kind == OccurrenceFeedKind.Gap)
        {
            subscriptions.Remove(Context.ConnectionId);
            return ToPayload(page);
        }

        subscriptions.Set(Context.ConnectionId, subscription with { Cursor = page.To });
        return ToPayload(page);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private static OccurrenceSnapshotPayload ToPayload(OccurrenceProjectionSnapshot snapshot) => new(
        snapshot.Cursor,
        snapshot.Occurrences.Select(ToPayload).ToArray());

    private static OccurrenceFeedPayload ToPayload(OccurrenceFeedPage page) => new(
        page.Kind.ToString(),
        page.From,
        page.To,
        page.Changes.Select(ToPayload).ToArray());

    private static OccurrencePayload ToPayload(OccurrenceProjectionRecord record) => new(
        record.Version.Value,
        record.Occurrence.OccurrenceId.Value,
        record.Occurrence.PointId.Value,
        record.Occurrence.Priority.ToString(),
        record.Occurrence.Condition.State.ToString(),
        record.Occurrence.Condition.Version.Value,
        record.Occurrence.Acknowledgement.State.ToString(),
        record.Occurrence.Acknowledgement.Version.Value,
        record.Occurrence.Assignment.Version.Value,
        record.Occurrence.Shelving.Version.Value,
        record.Occurrence.Suppression.Version.Value);
}

public static class EventEndpoints
{
    public static IServiceCollection AddEventServer(
        this IServiceCollection services,
        string connectionString,
        string databaseRole,
        EventDispatcherLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(sp => new EventStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            databaseRole,
            sp.GetRequiredService<IWallClock>(),
            limits));
        services.AddSingleton<AuthorizedEventDispatcher>();
        services.AddSingleton<EventRealtimeSubscriptionStore>();
        return services;
    }

    public static IEndpointRouteBuilder MapEventServer(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/events");
        group.MapGet("/{scopeId:guid}", QueryAsync);
        group.MapGet("/{scopeId:guid}/counters", CountAsync);
        endpoints.MapHub<EventRealtimeHub>("/hubs/events");
        return endpoints;
    }

    private static async Task<IResult> QueryAsync(
        Guid scopeId,
        Guid[]? pointIds,
        OperationalEventKind[]? kinds,
        AlarmPriority[]? priorities,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int pageSize,
        ulong? after,
        ulong? upper,
        HttpContext context,
        RequestSessionResolver sessions,
        AuthorizedEventDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            sessions.Resolve(context),
            Request(scopeId, pointIds, kinds, priorities, from, to, pageSize, after, upper),
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Ok(new EventPagePayload(
                result.Value.Events.Select(ToPayload).ToArray(),
                result.Value.UpperBound?.Value,
                result.Value.NextCursor?.After.Value,
                result.Value.NextCursor?.UpperBound.Value))
            : Problem(result.Error!);
    }

    private static async Task<IResult> CountAsync(
        Guid scopeId,
        Guid[]? pointIds,
        OperationalEventKind[]? kinds,
        AlarmPriority[]? priorities,
        DateTimeOffset? from,
        DateTimeOffset? to,
        HttpContext context,
        RequestSessionResolver sessions,
        AuthorizedEventDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.CountAsync(
            sessions.Resolve(context),
            Request(scopeId, pointIds, kinds, priorities, from, to, 1, null, null),
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);
    }

    private static EventQueryRequest Request(
        Guid scopeId,
        Guid[]? pointIds,
        OperationalEventKind[]? kinds,
        AlarmPriority[]? priorities,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int pageSize,
        ulong? after,
        ulong? upper) => new(
        RuntimeScopeId.From(scopeId),
        pointIds?.Select(PointId.From).ToHashSet() ?? [],
        kinds?.ToHashSet() ?? [],
        from,
        to,
        pageSize,
        after is not null && upper is not null
            ? new EventQueryCursor(new EventJournalPosition(after.Value), new EventJournalPosition(upper.Value))
            : null,
        priorities?.ToHashSet());

    private static EventPayload ToPayload(OperationalEventRecord record) => new(
        record.EventId.Value,
        record.Position.Value,
        record.PointId.Value,
        record.OccurrenceId.Value,
        record.SourceConditionVersion.Value,
        record.Priority.ToString(),
        record.Kind.ToString(),
        record.OccurredAt,
        record.AcceptedAt);

    private static IResult Problem(OperationError error) => Results.Problem(
        statusCode: error.Code.Value switch
        {
            "session.anonymous" or "session.revoked" or "session.expired" => StatusCodes.Status401Unauthorized,
            "permission.denied" or "permission.invalidated" => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        },
        title: error.Code.Value,
        detail: error.Message);
}

public sealed record EventPayload(
    Guid EventId,
    ulong Position,
    Guid PointId,
    Guid OccurrenceId,
    ulong SourceConditionVersion,
    string Priority,
    string Kind,
    DateTimeOffset OccurredAt,
    DateTimeOffset AcceptedAt);

public sealed record EventPagePayload(
    IReadOnlyList<EventPayload> Events,
    ulong? UpperBound,
    ulong? NextAfter,
    ulong? NextUpper);

public sealed record OccurrencePayload(
    ulong ProjectionVersion,
    Guid OccurrenceId,
    Guid PointId,
    string Priority,
    string ConditionState,
    ulong ConditionVersion,
    string AcknowledgementState,
    ulong AcknowledgementVersion,
    ulong AssignmentVersion,
    ulong ShelvingVersion,
    ulong SuppressionVersion);

public sealed record OccurrenceSnapshotPayload(ulong Cursor, IReadOnlyList<OccurrencePayload> Occurrences);

public sealed record OccurrenceFeedPayload(
    string Kind,
    ulong From,
    ulong To,
    IReadOnlyList<OccurrencePayload> Changes);
