using Dispatcher.Notifications;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

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
        services.AddSingleton<NotificationService>();
        services.AddSingleton<AuthorizedNotificationInbox>();
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
