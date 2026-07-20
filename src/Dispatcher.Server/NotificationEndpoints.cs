using Dispatcher.Notifications;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace Dispatcher.Server;

public sealed class AuthorizedNotificationInbox
{
    private readonly NotificationService notifications;
    private readonly WorkspaceStore workspace;
    private readonly IWallClock clock;

    public AuthorizedNotificationInbox(
        NotificationService notifications,
        WorkspaceStore workspace,
        IWallClock clock)
    {
        this.notifications = notifications;
        this.workspace = workspace;
        this.clock = clock;
    }

    public async Task<Result<IReadOnlyList<NotificationInboxItem>>> ReadAsync(
        SessionSnapshot? session,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(session, NotificationPermissions.InboxRead, cancellationToken)
            .ConfigureAwait(false);
        return context.IsFailure
            ? Result.Failure<IReadOnlyList<NotificationInboxItem>>(context.Error!)
            : await notifications.ReadInboxAsync(context.Value, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<NotificationInboxItem>> MarkReadAsync(
        SessionSnapshot? session,
        NotificationInboxItemId itemId,
        StateVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(session, NotificationPermissions.InboxRead, cancellationToken)
            .ConfigureAwait(false);
        return context.IsFailure
            ? Result.Failure<NotificationInboxItem>(context.Error!)
            : await notifications.MarkReadAsync(
                context.Value,
                itemId,
                expectedVersion,
                cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<NotificationSourceLink>> OpenSourceAsync(
        SessionSnapshot? session,
        NotificationInboxItemId itemId,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(session, NotificationPermissions.InboxRead, cancellationToken)
            .ConfigureAwait(false);
        return context.IsFailure
            ? Result.Failure<NotificationSourceLink>(context.Error!)
            : await notifications.OpenSourceLinkAsync(context.Value, itemId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<NotificationInboxCounterSnapshot>> ReadCountersAsync(
        SessionSnapshot? session,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(session, NotificationPermissions.InboxRead, cancellationToken)
            .ConfigureAwait(false);
        return context.IsFailure
            ? Result.Failure<NotificationInboxCounterSnapshot>(context.Error!)
            : await notifications.ReadCounterSnapshotAsync(context.Value, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<NotificationInboxCounterFeed>> PollCountersAsync(
        SessionSnapshot? session,
        ulong cursor,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(session, NotificationPermissions.InboxRead, cancellationToken)
            .ConfigureAwait(false);
        return context.IsFailure
            ? Result.Failure<NotificationInboxCounterFeed>(context.Error!)
            : await notifications.ReadCounterFeedAsync(context.Value, cursor, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<NotificationUserContext>> ResolveAsync(
        SessionSnapshot? session,
        PermissionCode permission,
        CancellationToken cancellationToken)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(session, permission, clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<NotificationUserContext>(authorization.Error!);
        }

        var account = await workspace.FindAccountAsync(
            authorization.Value.Session.SubjectId,
            cancellationToken).ConfigureAwait(false);
        return account is null
            ? Result.Failure<NotificationUserContext>(new OperationError(
                ErrorCode.From("workspace.account_not_found"),
                "Workspace account was not found for the session subject."))
            : Result.Success(new NotificationUserContext(authorization.Value.Session, account.PersonId));
    }
}

public static class NotificationEndpoints
{
    public static IServiceCollection AddNotificationServer(
        this IServiceCollection services,
        string connectionString,
        string databaseRole)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(sp => new NotificationStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            databaseRole,
            sp.GetRequiredService<IWallClock>()));
        services.AddSingleton(sp => new NotificationDeliveryStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            databaseRole,
            sp.GetRequiredService<IWallClock>()));
        services.AddSingleton<NotificationService>();
        services.AddSingleton<AuthorizedNotificationInbox>();
        services.AddSingleton<NotificationCounterSubscriptionStore>();
        return services;
    }

    public static IServiceCollection AddSmtpNotificationProvider(
        this IServiceCollection services,
        SmtpProviderConfiguration configuration,
        NotificationDeliveryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(policy);
        services.AddSingleton(configuration);
        services.AddSingleton(policy);
        services.AddSingleton<INotificationSecretResolver, EnvironmentNotificationSecretResolver>();
        services.AddSingleton<ISmtpClientAdapter, SystemSmtpClientAdapter>();
        services.AddSingleton<SmtpNotificationProvider>();
        services.AddSingleton<NotificationDeliveryWorker>();
        return services;
    }

    public static IEndpointRouteBuilder MapNotificationServer(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/notifications");
        group.MapGet("/inbox", async (
            HttpContext context,
            RequestSessionResolver sessions,
            AuthorizedNotificationInbox inbox,
            CancellationToken cancellationToken) =>
            ToHttpResult(await inbox.ReadAsync(
                sessions.Resolve(context),
                cancellationToken).ConfigureAwait(false)));
        group.MapPost("/inbox/{itemId:guid}/read", async (
            Guid itemId,
            MarkNotificationReadRequest request,
            HttpContext context,
            RequestSessionResolver sessions,
            AuthorizedNotificationInbox inbox,
            CancellationToken cancellationToken) =>
            ToHttpResult(await inbox.MarkReadAsync(
                sessions.Resolve(context),
                NotificationInboxItemId.From(itemId),
                StateVersion.From(request.ExpectedVersion),
                cancellationToken).ConfigureAwait(false)));
        group.MapGet("/inbox/{itemId:guid}/source", async (
            Guid itemId,
            HttpContext context,
            RequestSessionResolver sessions,
            AuthorizedNotificationInbox inbox,
            CancellationToken cancellationToken) =>
            ToHttpResult(await inbox.OpenSourceAsync(
                sessions.Resolve(context),
                NotificationInboxItemId.From(itemId),
                cancellationToken).ConfigureAwait(false)));
        endpoints.MapHub<NotificationCounterHub>("/hubs/notification-counters");
        return endpoints;
    }

    private static IResult ToHttpResult<T>(Result<T> result) => result.IsSuccess
        ? Results.Ok(result.Value)
        : Results.Problem(
            statusCode: result.Error!.Code.Value switch
            {
                "session.anonymous" or "session.expired" or "session.revoked" => StatusCodes.Status401Unauthorized,
                "permission.denied" => StatusCodes.Status403Forbidden,
                "notification.inbox_not_found" or "workspace.account_not_found" => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status409Conflict,
            },
            title: result.Error.Code.Value,
            detail: result.Error.Message);
}

public sealed record MarkNotificationReadRequest(ulong ExpectedVersion);

public sealed record NotificationCounterPayload(ulong Cursor, long TotalCount, long UnreadCount);

public sealed record NotificationCounterPollPayload(
    string Kind,
    ulong From,
    ulong To,
    IReadOnlyList<NotificationCounterPayload> Changes);

public sealed record NotificationCounterSubscription(SessionId SessionId, ulong Cursor);

public sealed class NotificationCounterSubscriptionStore
{
    private readonly ConcurrentDictionary<string, NotificationCounterSubscription> subscriptions = new();
    public void Set(string connectionId, NotificationCounterSubscription subscription) =>
        subscriptions[connectionId] = subscription;
    public bool TryGet(string connectionId, out NotificationCounterSubscription? subscription) =>
        subscriptions.TryGetValue(connectionId, out subscription);
    public void Remove(string connectionId) => subscriptions.TryRemove(connectionId, out _);
}

public sealed class NotificationCounterHub : Hub
{
    private readonly RequestSessionResolver sessions;
    private readonly AuthorizedNotificationInbox inbox;
    private readonly NotificationCounterSubscriptionStore subscriptions;

    public NotificationCounterHub(
        RequestSessionResolver sessions,
        AuthorizedNotificationInbox inbox,
        NotificationCounterSubscriptionStore subscriptions)
    {
        this.sessions = sessions;
        this.inbox = inbox;
        this.subscriptions = subscriptions;
    }

    public async Task<NotificationCounterPayload> Bootstrap()
    {
        var session = sessions.Resolve(Context.GetHttpContext());
        var result = await inbox.ReadCountersAsync(session, Context.ConnectionAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new HubException(result.Error!.Code.Value);
        }

        subscriptions.Set(Context.ConnectionId, new NotificationCounterSubscription(session!.Id, result.Value.Cursor));
        return ToPayload(result.Value);
    }

    public async Task<NotificationCounterPollPayload> Poll(ulong cursor)
    {
        if (!subscriptions.TryGet(Context.ConnectionId, out var subscription) || subscription!.Cursor != cursor)
        {
            subscriptions.Remove(Context.ConnectionId);
            return new NotificationCounterPollPayload(NotificationCounterFeedKind.Gap.ToString(), cursor, cursor, []);
        }

        var session = sessions.Resolve(Context.GetHttpContext());
        if (session is null || session.Id != subscription.SessionId)
        {
            subscriptions.Remove(Context.ConnectionId);
            return new NotificationCounterPollPayload("PermissionInvalidated", cursor, cursor, []);
        }

        var result = await inbox.PollCountersAsync(session, cursor, Context.ConnectionAborted).ConfigureAwait(false);
        if (result.IsFailure)
        {
            subscriptions.Remove(Context.ConnectionId);
            return new NotificationCounterPollPayload("PermissionInvalidated", cursor, cursor, []);
        }

        if (result.Value.Kind == NotificationCounterFeedKind.Gap)
        {
            subscriptions.Remove(Context.ConnectionId);
            return ToPayload(result.Value);
        }

        subscriptions.Set(Context.ConnectionId, subscription with { Cursor = result.Value.To });
        return ToPayload(result.Value);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private static NotificationCounterPayload ToPayload(NotificationInboxCounterSnapshot value) =>
        new(value.Cursor, value.TotalCount, value.UnreadCount);

    private static NotificationCounterPollPayload ToPayload(NotificationInboxCounterFeed value) =>
        new(value.Kind.ToString(), value.From, value.To, value.Changes.Select(ToPayload).ToArray());
}
