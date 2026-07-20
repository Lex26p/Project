using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;

namespace Dispatcher.Notifications;

public readonly record struct NotificationPolicyId
{
    private readonly CanonicalId<NotificationPolicyId> value;
    private NotificationPolicyId(CanonicalId<NotificationPolicyId> value) => this.value = value;
    public Guid Value => value.Value;
    public static NotificationPolicyId New() => new(CanonicalId.New<NotificationPolicyId>());
    public static NotificationPolicyId From(Guid value) => new(CanonicalId.From<NotificationPolicyId>(value));
}

public readonly record struct NotificationRuleId
{
    private readonly CanonicalId<NotificationRuleId> value;
    private NotificationRuleId(CanonicalId<NotificationRuleId> value) => this.value = value;
    public Guid Value => value.Value;
    public static NotificationRuleId New() => new(CanonicalId.New<NotificationRuleId>());
    public static NotificationRuleId From(Guid value) => new(CanonicalId.From<NotificationRuleId>(value));
}

public readonly record struct NotificationSubscriptionId
{
    private readonly CanonicalId<NotificationSubscriptionId> value;
    private NotificationSubscriptionId(CanonicalId<NotificationSubscriptionId> value) => this.value = value;
    public Guid Value => value.Value;
    public static NotificationSubscriptionId New() => new(CanonicalId.New<NotificationSubscriptionId>());
    public static NotificationSubscriptionId From(Guid value) => new(CanonicalId.From<NotificationSubscriptionId>(value));
}

public readonly record struct NotificationInboxItemId
{
    private readonly CanonicalId<NotificationInboxItemId> value;
    private NotificationInboxItemId(CanonicalId<NotificationInboxItemId> value) => this.value = value;
    public Guid Value => value.Value;
    public static NotificationInboxItemId New() => new(CanonicalId.New<NotificationInboxItemId>());
    public static NotificationInboxItemId From(Guid value) => new(CanonicalId.From<NotificationInboxItemId>(value));
}

public enum NotificationChannel
{
    Inbox = 1,
    Email = 2,
}

public sealed record MandatoryNotificationRule
{
    public MandatoryNotificationRule(
        NotificationRuleId ruleId,
        AlarmPriority minimumPriority,
        IReadOnlyCollection<PersonId> recipients,
        IReadOnlyCollection<NotificationChannel> channels)
    {
        _ = ruleId.Value;
        if (!Enum.IsDefined(minimumPriority) || recipients is null || recipients.Count == 0 ||
            channels is null || channels.Count == 0 || channels.Any(channel => !Enum.IsDefined(channel)) ||
            recipients.Select(person => person.Value).Distinct().Count() != recipients.Count ||
            channels.Distinct().Count() != channels.Count)
        {
            throw new ArgumentException("Mandatory notification rule is invalid.");
        }

        RuleId = ruleId;
        MinimumPriority = minimumPriority;
        Recipients = recipients.OrderBy(person => person.Value).ToArray();
        Channels = channels.Order().ToArray();
    }

    public NotificationRuleId RuleId { get; }
    public AlarmPriority MinimumPriority { get; }
    public IReadOnlyList<PersonId> Recipients { get; }
    public IReadOnlyList<NotificationChannel> Channels { get; }
}

public sealed record NotificationPolicy
{
    public NotificationPolicy(
        NotificationPolicyId policyId,
        RuntimeScopeId scopeId,
        RevisionNumber revision,
        IReadOnlyCollection<MandatoryNotificationRule> mandatoryRules)
    {
        _ = policyId.Value;
        _ = scopeId.Value;
        if (!revision.IsDefined)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        ArgumentNullException.ThrowIfNull(mandatoryRules);
        var ordered = mandatoryRules.OrderBy(rule => rule.RuleId.Value).ToArray();
        if (ordered.Select(rule => rule.RuleId).Distinct().Count() != ordered.Length)
        {
            throw new ArgumentException("Mandatory rule identities must be unique.", nameof(mandatoryRules));
        }

        PolicyId = policyId;
        ScopeId = scopeId;
        Revision = revision;
        MandatoryRules = ordered;
    }

    public NotificationPolicyId PolicyId { get; }
    public RuntimeScopeId ScopeId { get; }
    public RevisionNumber Revision { get; }
    public IReadOnlyList<MandatoryNotificationRule> MandatoryRules { get; }
}

public sealed record NotificationSchedule
{
    public NotificationSchedule(IReadOnlyCollection<DayOfWeek> activeDays, TimeOnly startsAtUtc, TimeOnly endsAtUtc)
    {
        ArgumentNullException.ThrowIfNull(activeDays);
        if (activeDays.Count == 0 || activeDays.Any(day => !Enum.IsDefined(day)) ||
            activeDays.Distinct().Count() != activeDays.Count || startsAtUtc == endsAtUtc)
        {
            throw new ArgumentException("Notification schedule is invalid.");
        }

        ActiveDays = activeDays.Order().ToArray();
        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
    }

    public IReadOnlyList<DayOfWeek> ActiveDays { get; }
    public TimeOnly StartsAtUtc { get; }
    public TimeOnly EndsAtUtc { get; }

    public bool Contains(DateTimeOffset timestamp)
    {
        var utc = timestamp.UtcDateTime;
        var time = TimeOnly.FromDateTime(utc);
        if (StartsAtUtc < EndsAtUtc)
        {
            return ActiveDays.Contains(utc.DayOfWeek) && time >= StartsAtUtc && time < EndsAtUtc;
        }

        return time >= StartsAtUtc
            ? ActiveDays.Contains(utc.DayOfWeek)
            : ActiveDays.Contains(utc.AddDays(-1).DayOfWeek) && time < EndsAtUtc;
    }
}

public sealed record NotificationQuietPeriod(NotificationSchedule Schedule);

public sealed record NotificationAbsence
{
    public NotificationAbsence(DateTimeOffset startsAt, DateTimeOffset endsAt, PersonId coveragePersonId)
    {
        _ = coveragePersonId.Value;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(endsAt, startsAt);

        StartsAt = startsAt;
        EndsAt = endsAt;
        CoveragePersonId = coveragePersonId;
    }

    public DateTimeOffset StartsAt { get; }
    public DateTimeOffset EndsAt { get; }
    public PersonId CoveragePersonId { get; }
    public bool Contains(DateTimeOffset timestamp) => timestamp >= StartsAt && timestamp < EndsAt;
}

public sealed record NotificationChannelPreference(NotificationChannel Channel, bool Enabled);

public sealed record PersonalNotificationSettings
{
    public PersonalNotificationSettings(
        PersonId personId,
        StateVersion version,
        NotificationSchedule? schedule,
        IReadOnlyCollection<NotificationQuietPeriod> quietPeriods,
        NotificationAbsence? absence,
        IReadOnlyCollection<NotificationChannelPreference> channelPreferences)
    {
        _ = personId.Value;
        ArgumentNullException.ThrowIfNull(quietPeriods);
        ArgumentNullException.ThrowIfNull(channelPreferences);
        if (absence?.CoveragePersonId == personId ||
            channelPreferences.Any(item => !Enum.IsDefined(item.Channel)) ||
            channelPreferences.Select(item => item.Channel).Distinct().Count() != channelPreferences.Count)
        {
            throw new ArgumentException("Personal notification settings are invalid.");
        }

        PersonId = personId;
        Version = version;
        Schedule = schedule;
        QuietPeriods = quietPeriods.ToArray();
        Absence = absence;
        ChannelPreferences = channelPreferences.OrderBy(item => item.Channel).ToArray();
    }

    public PersonId PersonId { get; }
    public StateVersion Version { get; }
    public NotificationSchedule? Schedule { get; }
    public IReadOnlyList<NotificationQuietPeriod> QuietPeriods { get; }
    public NotificationAbsence? Absence { get; }
    public IReadOnlyList<NotificationChannelPreference> ChannelPreferences { get; }
}

public sealed record NotificationSubscription
{
    public NotificationSubscription(
        NotificationSubscriptionId subscriptionId,
        PersonId personId,
        RuntimeScopeId scopeId,
        StateVersion version,
        AlarmPriority minimumPriority,
        IReadOnlyCollection<PointId> pointIds,
        IReadOnlyCollection<NotificationChannel> channels,
        bool enabled)
    {
        _ = subscriptionId.Value;
        _ = personId.Value;
        _ = scopeId.Value;
        ArgumentNullException.ThrowIfNull(pointIds);
        ArgumentNullException.ThrowIfNull(channels);
        if (!Enum.IsDefined(minimumPriority) || channels.Count == 0 ||
            pointIds.Select(point => point.Value).Distinct().Count() != pointIds.Count ||
            channels.Any(channel => !Enum.IsDefined(channel)) || channels.Distinct().Count() != channels.Count)
        {
            throw new ArgumentException("Notification subscription is invalid.");
        }

        SubscriptionId = subscriptionId;
        PersonId = personId;
        ScopeId = scopeId;
        Version = version;
        MinimumPriority = minimumPriority;
        PointIds = pointIds.OrderBy(point => point.Value).ToArray();
        Channels = channels.Order().ToArray();
        Enabled = enabled;
    }

    public NotificationSubscriptionId SubscriptionId { get; }
    public PersonId PersonId { get; }
    public RuntimeScopeId ScopeId { get; }
    public StateVersion Version { get; }
    public AlarmPriority MinimumPriority { get; }
    public IReadOnlyList<PointId> PointIds { get; }
    public IReadOnlyList<NotificationChannel> Channels { get; }
    public bool Enabled { get; }
}

public sealed record NotificationSourceLink(
    OperationalEventId EventId,
    AlarmOccurrenceId OccurrenceId,
    RuntimeScopeId ScopeId,
    PointId PointId,
    string Route,
    IReadOnlyList<PermissionCode> RequiredPermissions);

public sealed record NotificationCandidate(
    OperationalEventId EventId,
    RuntimeScopeId ScopeId,
    PointId PointId,
    AlarmOccurrenceId OccurrenceId,
    AlarmPriority Priority,
    string Title,
    DateTimeOffset OccurredAt,
    NotificationSourceLink SourceLink)
{
    public static NotificationCandidate FromOperationalEvent(OperationalEventRecord record, string title)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(title));
        }

        return new NotificationCandidate(
            record.EventId,
            record.ScopeId,
            record.PointId,
            record.OccurrenceId,
            record.Priority,
            title.Trim(),
            record.OccurredAt,
            new NotificationSourceLink(
                record.EventId,
                record.OccurrenceId,
                record.ScopeId,
                record.PointId,
                $"/events/{record.EventId.Value}",
                [
                    PermissionCode.From("events.dispatcher.read"),
                    PermissionCode.From($"runtime.point.p{record.PointId.Value:N}.read"),
                ]));
    }
}

public sealed record NotificationRoute(
    PersonId RecipientId,
    NotificationChannel Channel,
    bool Mandatory,
    PersonId? CoveredPersonId);

public sealed record NotificationInboxItem(
    NotificationInboxItemId ItemId,
    PersonId RecipientId,
    OperationalEventId EventId,
    string Title,
    AlarmPriority Priority,
    NotificationSourceLink SourceLink,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    StateVersion Version);

public static class NotificationPermissions
{
    public static PermissionCode ManagePolicy(RuntimeScopeId scopeId) =>
        PermissionCode.From($"notification.scope.s{scopeId.Value:N}.policy.manage");

    public static PermissionCode Subscribe { get; } = PermissionCode.From("notification.personal.subscribe");
    public static PermissionCode Preferences { get; } = PermissionCode.From("notification.personal.preferences");
    public static PermissionCode InboxRead { get; } = PermissionCode.From("notification.inbox.read");
}
