using System.Collections.Concurrent;
using Dispatcher.Core;
using Dispatcher.Platform;
using Microsoft.AspNetCore.SignalR;

namespace Dispatcher.Server;

internal sealed record RealtimeSubscription(
    SessionId SessionId,
    RuntimeScopeId ScopeId,
    ulong CoreCursor,
    ulong WebCursor,
    IReadOnlySet<Guid> PointIds,
    bool RestrictedToPointIds,
    IReadOnlySet<PermissionCode> PermissionGrants,
    IReadOnlySet<PermissionCode> PermissionDenials);

public sealed class RealtimeSubscriptionStore
{
    private readonly ConcurrentDictionary<string, RealtimeSubscription> subscriptions = new();

    internal void Set(string connectionId, RealtimeSubscription subscription) =>
        subscriptions[connectionId] = subscription;

    internal bool TryGet(string connectionId, out RealtimeSubscription? subscription) =>
        subscriptions.TryGetValue(connectionId, out subscription);

    internal void Remove(string connectionId) => subscriptions.TryRemove(connectionId, out _);
}

public sealed class RuntimeRealtimeHub : Hub
{
    private readonly RequestSessionResolver sessionResolver;
    private readonly AuthorizedRuntimeReader reader;
    private readonly RealtimeSubscriptionStore subscriptions;

    public RuntimeRealtimeHub(
        RequestSessionResolver sessionResolver,
        AuthorizedRuntimeReader reader,
        RealtimeSubscriptionStore subscriptions)
    {
        this.sessionResolver = sessionResolver;
        this.reader = reader;
        this.subscriptions = subscriptions;
    }

    public Task<RuntimeSnapshotPayload> Bootstrap(Guid scopeId)
        => BootstrapCore(scopeId, null);

    public Task<RuntimeSnapshotPayload> BootstrapPoints(Guid scopeId, IReadOnlyCollection<Guid> pointIds)
    {
        ArgumentNullException.ThrowIfNull(pointIds);
        return BootstrapCore(scopeId, pointIds.ToHashSet());
    }

    private async Task<RuntimeSnapshotPayload> BootstrapCore(
        Guid scopeId,
        IReadOnlySet<Guid>? pointIds)
    {
        var session = sessionResolver.Resolve(Context.GetHttpContext());
        var result = await reader.ReadSnapshotAsync(
            session,
            RuntimeScopeId.From(scopeId),
            pointIds,
            Context.ConnectionAborted);
        if (result.IsFailure)
        {
            throw new HubException(result.Error!.Code.Value);
        }

        var snapshot = result.Value;
        subscriptions.Set(Context.ConnectionId, new RealtimeSubscription(
            session!.Id,
            RuntimeScopeId.From(scopeId),
            snapshot.CoreCursor,
            0,
            snapshot.PointIds,
            pointIds is not null,
            session!.Permissions.Grants.ToHashSet(),
            session.Permissions.Denials.ToHashSet()));
        return snapshot.Payload;
    }

    public async Task<RealtimePollPayload> Poll(Guid scopeId, ulong cursor)
    {
        if (!subscriptions.TryGet(Context.ConnectionId, out var subscription) ||
            subscription!.ScopeId != RuntimeScopeId.From(scopeId) ||
            subscription.WebCursor != cursor)
        {
            subscriptions.Remove(Context.ConnectionId);
            return new RealtimePollPayload(RealtimePollKind.Gap);
        }

        var session = sessionResolver.Resolve(Context.GetHttpContext());
        if (session is null ||
            session.Id != subscription.SessionId ||
            !session.Permissions.Grants.SetEquals(subscription.PermissionGrants) ||
            !session.Permissions.Denials.SetEquals(subscription.PermissionDenials))
        {
            subscriptions.Remove(Context.ConnectionId);
            return new RealtimePollPayload(RealtimePollKind.PermissionInvalidated);
        }

        var currentSnapshot = await reader.ReadSnapshotAsync(
            session,
            subscription.ScopeId,
            subscription.RestrictedToPointIds ? subscription.PointIds : null,
            Context.ConnectionAborted);
        if (currentSnapshot.IsFailure)
        {
            subscriptions.Remove(Context.ConnectionId);
            return new RealtimePollPayload(
                IsAuthorizationFailure(currentSnapshot.Error?.Code.Value)
                    ? RealtimePollKind.PermissionInvalidated
                    : RealtimePollKind.Gap);
        }
        if (!currentSnapshot.Value.PointIds.SetEquals(subscription.PointIds))
        {
            subscriptions.Remove(Context.ConnectionId);
            return new RealtimePollPayload(RealtimePollKind.PermissionInvalidated);
        }

        var result = await reader.ReadDeltaAsync(
            session,
            subscription.ScopeId,
            subscription.CoreCursor,
            subscription.PointIds,
            Context.ConnectionAborted);
        if (result.IsFailure)
        {
            subscriptions.Remove(Context.ConnectionId);
            var kind = result.Error!.Code.Value == "runtime.cursor_gap"
                ? RealtimePollKind.Gap
                : IsAuthorizationFailure(result.Error.Code.Value)
                    ? RealtimePollKind.PermissionInvalidated
                    : RealtimePollKind.Gap;
            return new RealtimePollPayload(kind);
        }

        var delta = result.Value;
        if (delta.Changes.Count == 0)
        {
            subscriptions.Set(Context.ConnectionId, subscription with { CoreCursor = delta.CoreCursor });
            return new RealtimePollPayload(RealtimePollKind.NoChange);
        }

        var nextWebCursor = checked(subscription.WebCursor + 1);
        subscriptions.Set(Context.ConnectionId, subscription with
        {
            CoreCursor = delta.CoreCursor,
            WebCursor = nextWebCursor,
        });
        return new RealtimePollPayload(
            RealtimePollKind.Delta,
            new RuntimeDeltaPayload(scopeId, subscription.WebCursor, nextWebCursor, delta.Changes));
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private static bool IsAuthorizationFailure(string? code) =>
        code is "session.anonymous" or
            "session.revoked" or
            "session.expired" or
            "permission.denied";
}
