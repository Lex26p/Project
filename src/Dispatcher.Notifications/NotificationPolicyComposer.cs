using Dispatcher.Workspace;

namespace Dispatcher.Notifications;

public static class NotificationPolicyComposer
{
    public static IReadOnlyList<NotificationRoute> Compose(
        NotificationPolicy policy,
        IReadOnlyCollection<NotificationSubscription> subscriptions,
        IReadOnlyDictionary<PersonId, PersonalNotificationSettings> settings,
        NotificationCandidate candidate,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(candidate);
        if (policy.ScopeId != candidate.ScopeId)
        {
            throw new ArgumentException("Notification candidate belongs to another policy scope.", nameof(candidate));
        }

        var routes = new Dictionary<(PersonId Recipient, NotificationChannel Channel), NotificationRoute>();
        foreach (var rule in policy.MandatoryRules.Where(rule => candidate.Priority >= rule.MinimumPriority))
        {
            foreach (var personId in rule.Recipients)
            {
                foreach (var channel in rule.Channels)
                {
                    Add(routes, new NotificationRoute(personId, channel, true, null));
                    if (ActiveCoverage(settings, personId, now) is { } coverage)
                    {
                        Add(routes, new NotificationRoute(coverage, channel, true, personId));
                    }
                }
            }
        }

        foreach (var subscription in subscriptions.Where(subscription =>
                     subscription.Enabled &&
                     subscription.ScopeId == candidate.ScopeId &&
                     candidate.Priority >= subscription.MinimumPriority &&
                     (subscription.PointIds.Count == 0 || subscription.PointIds.Contains(candidate.PointId))))
        {
            settings.TryGetValue(subscription.PersonId, out var personal);
            if (!PersonalWindowAllows(personal, now))
            {
                continue;
            }

            var recipient = ActiveCoverage(settings, subscription.PersonId, now) ?? subscription.PersonId;
            PersonId? covered = recipient == subscription.PersonId ? null : subscription.PersonId;
            foreach (var channel in subscription.Channels.Where(channel => PersonalChannelAllows(personal, channel)))
            {
                Add(routes, new NotificationRoute(recipient, channel, false, covered));
            }
        }

        return routes.Values
            .OrderBy(route => route.RecipientId.Value)
            .ThenBy(route => route.Channel)
            .ToArray();
    }

    private static void Add(
        Dictionary<(PersonId Recipient, NotificationChannel Channel), NotificationRoute> routes,
        NotificationRoute route)
    {
        var key = (route.RecipientId, route.Channel);
        if (!routes.TryGetValue(key, out var current) || route.Mandatory && !current.Mandatory)
        {
            routes[key] = route;
        }
    }

    private static PersonId? ActiveCoverage(
        IReadOnlyDictionary<PersonId, PersonalNotificationSettings> settings,
        PersonId personId,
        DateTimeOffset now) =>
        settings.TryGetValue(personId, out var personal) && personal.Absence?.Contains(now) == true
            ? personal.Absence.CoveragePersonId
            : null;

    private static bool PersonalWindowAllows(PersonalNotificationSettings? settings, DateTimeOffset now) =>
        settings is null ||
        (settings.Schedule?.Contains(now) != false &&
         !settings.QuietPeriods.Any(period => period.Schedule.Contains(now)));

    private static bool PersonalChannelAllows(
        PersonalNotificationSettings? settings,
        NotificationChannel channel) =>
        settings?.ChannelPreferences.FirstOrDefault(item => item.Channel == channel)?.Enabled ??
        channel == NotificationChannel.Inbox;
}
