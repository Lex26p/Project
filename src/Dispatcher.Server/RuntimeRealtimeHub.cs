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
    IReadOnlySet<Guid> PointIds);

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
    {
        var session = sessionResolver.Resolve(Context.GetHttpContext());
        var result = reader.ReadSnapshot(session, RuntimeScopeId.From(scopeId));
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
            snapshot.PointIds));
        return Task.FromResult(snapshot.Payload);
    }

    public Task<RealtimePollPayload> Poll(Guid scopeId, ulong cursor)
    {
        if (!subscriptions.TryGet(Context.ConnectionId, out var subscription) ||
            subscription!.ScopeId != RuntimeScopeId.From(scopeId) ||
            subscription.WebCursor != cursor)
        {
            subscriptions.Remove(Context.ConnectionId);
            return Task.FromResult(new RealtimePollPayload(RealtimePollKind.Gap));
        }

        var session = sessionResolver.Resolve(Context.GetHttpContext());
        if (session is null || session.Id != subscription.SessionId)
        {
            subscriptions.Remove(Context.ConnectionId);
            return Task.FromResult(new RealtimePollPayload(RealtimePollKind.PermissionInvalidated));
        }

        var currentSnapshot = reader.ReadSnapshot(session, subscription.ScopeId);
        if (currentSnapshot.IsFailure || !currentSnapshot.Value.PointIds.SetEquals(subscription.PointIds))
        {
            subscriptions.Remove(Context.ConnectionId);
            return Task.FromResult(new RealtimePollPayload(RealtimePollKind.PermissionInvalidated));
        }

        var result = reader.ReadDelta(session, subscription.ScopeId, subscription.CoreCursor);
        if (result.IsFailure)
        {
            subscriptions.Remove(Context.ConnectionId);
            var kind = result.Error!.Code.Value == "runtime.cursor_gap"
                ? RealtimePollKind.Gap
                : RealtimePollKind.PermissionInvalidated;
            return Task.FromResult(new RealtimePollPayload(kind));
        }

        var delta = result.Value;
        if (delta.Changes.Count == 0)
        {
            subscriptions.Set(Context.ConnectionId, subscription with { CoreCursor = delta.CoreCursor });
            return Task.FromResult(new RealtimePollPayload(RealtimePollKind.NoChange));
        }

        var nextWebCursor = checked(subscription.WebCursor + 1);
        subscriptions.Set(Context.ConnectionId, subscription with
        {
            CoreCursor = delta.CoreCursor,
            WebCursor = nextWebCursor,
        });
        return Task.FromResult(new RealtimePollPayload(
            RealtimePollKind.Delta,
            new RuntimeDeltaPayload(scopeId, subscription.WebCursor, nextWebCursor, delta.Changes)));
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
