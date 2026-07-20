using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;

namespace Dispatcher.Notifications;

public sealed record NotificationUserContext(SessionSnapshot Session, PersonId PersonId);

public sealed class NotificationService
{
    private readonly NotificationStore store;
    private readonly IWallClock clock;

    public NotificationService(NotificationStore store, IWallClock clock)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<Result<NotificationPolicy>> PublishPolicyAsync(
        SessionSnapshot? session,
        NotificationPolicy policy,
        RevisionNumber? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var authorization = SessionAuthorization.AuthorizeMutation(
            session,
            NotificationPermissions.ManagePolicy(policy.ScopeId),
            clock);
        return authorization.IsFailure
            ? Result.Failure<NotificationPolicy>(authorization.Error!)
            : await store.SavePolicyAsync(policy, expectedRevision, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<PersonalNotificationSettings>> SaveSettingsAsync(
        NotificationUserContext? context,
        PersonalNotificationSettings settings,
        StateVersion? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var authorization = AuthorizePersonal(context, settings.PersonId, NotificationPermissions.Preferences);
        return authorization.IsFailure
            ? Result.Failure<PersonalNotificationSettings>(authorization.Error!)
            : await store.SaveSettingsAsync(settings, expectedVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<NotificationSubscription>> SaveSubscriptionAsync(
        NotificationUserContext? context,
        NotificationSubscription subscription,
        StateVersion? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var authorization = AuthorizePersonal(context, subscription.PersonId, NotificationPermissions.Subscribe);
        return authorization.IsFailure
            ? Result.Failure<NotificationSubscription>(authorization.Error!)
            : await store.SaveSubscriptionAsync(subscription, expectedVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<NotificationAcceptance>> RouteEventAsync(
        OperationalEventRecord operationalEvent,
        string title,
        CancellationToken cancellationToken = default)
    {
        var candidate = NotificationCandidate.FromOperationalEvent(operationalEvent, title);
        var policy = await store.ReadPolicyAsync(candidate.ScopeId, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            return Failure<NotificationAcceptance>(
                "notification.policy_not_found",
                "Notification policy was not found for the event scope.");
        }

        var subscriptions = await store.ReadSubscriptionsAsync(candidate.ScopeId, cancellationToken).ConfigureAwait(false);
        var persons = policy.MandatoryRules.SelectMany(rule => rule.Recipients)
            .Concat(subscriptions.Select(subscription => subscription.PersonId))
            .Distinct()
            .ToArray();
        var settings = await store.ReadSettingsAsync(persons, cancellationToken).ConfigureAwait(false);
        var routes = NotificationPolicyComposer.Compose(policy, subscriptions, settings, candidate, clock.GetUtcNow());
        return await store.AcceptAsync(candidate, routes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<IReadOnlyList<NotificationInboxItem>>> ReadInboxAsync(
        NotificationUserContext? context,
        CancellationToken cancellationToken = default)
    {
        var authorization = AuthorizePersonal(
            context,
            context?.PersonId,
            NotificationPermissions.InboxRead,
            mutation: false);
        return authorization.IsFailure
            ? Result.Failure<IReadOnlyList<NotificationInboxItem>>(authorization.Error!)
            : Result.Success(await store.ReadInboxAsync(
                context!.PersonId,
                cancellationToken).ConfigureAwait(false));
    }

    public async Task<Result<NotificationInboxItem>> MarkReadAsync(
        NotificationUserContext? context,
        NotificationInboxItemId itemId,
        StateVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var authorization = AuthorizePersonal(context, context?.PersonId, NotificationPermissions.InboxRead);
        return authorization.IsFailure
            ? Result.Failure<NotificationInboxItem>(authorization.Error!)
            : await store.MarkReadAsync(
                context!.PersonId,
                itemId,
                expectedVersion,
                cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<NotificationSourceLink>> OpenSourceLinkAsync(
        NotificationUserContext? context,
        NotificationInboxItemId itemId,
        CancellationToken cancellationToken = default)
    {
        var inboxAuthorization = AuthorizePersonal(
            context,
            context?.PersonId,
            NotificationPermissions.InboxRead,
            mutation: false);
        if (inboxAuthorization.IsFailure)
        {
            return Result.Failure<NotificationSourceLink>(inboxAuthorization.Error!);
        }

        var item = await store.ReadInboxItemAsync(context!.PersonId, itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Failure<NotificationSourceLink>("notification.inbox_not_found", "Inbox item was not found.");
        }

        foreach (var permission in item.SourceLink.RequiredPermissions)
        {
            var authorization = SessionAuthorization.AuthorizeAccess(context.Session, permission, clock);
            if (authorization.IsFailure)
            {
                return Result.Failure<NotificationSourceLink>(authorization.Error!);
            }
        }

        return Result.Success(item.SourceLink);
    }

    private Result AuthorizePersonal(
        NotificationUserContext? context,
        PersonId? targetPersonId,
        PermissionCode permission,
        bool mutation = true)
    {
        if (context is null || targetPersonId is null || context.PersonId != targetPersonId)
        {
            return Result.Failure(new OperationError(
                ErrorCode.From("permission.denied"),
                "Personal notification resource belongs to another person."));
        }

        if (mutation)
        {
            var authorization = SessionAuthorization.AuthorizeMutation(context.Session, permission, clock);
            return authorization.IsFailure ? Result.Failure(authorization.Error!) : Result.Success();
        }

        var access = SessionAuthorization.AuthorizeAccess(context.Session, permission, clock);
        return access.IsFailure ? Result.Failure(access.Error!) : Result.Success();
    }

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));
}
