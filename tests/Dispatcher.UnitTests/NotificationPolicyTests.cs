using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Notifications;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class NotificationPolicyTests
{
    private static readonly RuntimeScopeId ScopeId = RuntimeScopeId.From(
        Guid.Parse("f1000000-0000-0000-0000-000000000001"));
    private static readonly PointId PointId = Dispatcher.Semantics.PointId.From(
        Guid.Parse("f2000000-0000-0000-0000-000000000001"));
    private static readonly PersonId Primary = PersonId.From(
        Guid.Parse("f3000000-0000-0000-0000-000000000001"));
    private static readonly PersonId Coverage = PersonId.From(
        Guid.Parse("f3000000-0000-0000-0000-000000000002"));
    private static readonly PersonId Personal = PersonId.From(
        Guid.Parse("f3000000-0000-0000-0000-000000000003"));
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PersonalPreferencesQuietPeriodAndAbsenceCannotWeakenMandatoryRoute()
    {
        var policy = new NotificationPolicy(
            NotificationPolicyId.New(),
            ScopeId,
            RevisionNumber.Initial,
            [new MandatoryNotificationRule(
                NotificationRuleId.New(),
                AlarmPriority.High,
                [Primary],
                [NotificationChannel.Inbox, NotificationChannel.Email])]);
        var subscriptions = new[]
        {
            Subscription(Primary, [NotificationChannel.Inbox, NotificationChannel.Email]),
            Subscription(Personal, [NotificationChannel.Inbox, NotificationChannel.Email]),
        };
        var settings = new Dictionary<PersonId, PersonalNotificationSettings>
        {
            [Primary] = new PersonalNotificationSettings(
                Primary,
                StateVersion.Initial,
                null,
                [new NotificationQuietPeriod(new NotificationSchedule(
                    Enum.GetValues<DayOfWeek>(),
                    new TimeOnly(0, 0),
                    new TimeOnly(23, 59)))],
                new NotificationAbsence(Now.AddHours(-1), Now.AddHours(1), Coverage),
                [
                    new NotificationChannelPreference(NotificationChannel.Inbox, false),
                    new NotificationChannelPreference(NotificationChannel.Email, false),
                ]),
        };

        var routes = NotificationPolicyComposer.Compose(policy, subscriptions, settings, Candidate(), Now);

        Assert.Contains(routes, route => route.RecipientId == Primary && route.Channel == NotificationChannel.Inbox && route.Mandatory);
        Assert.Contains(routes, route => route.RecipientId == Primary && route.Channel == NotificationChannel.Email && route.Mandatory);
        Assert.Contains(routes, route => route.RecipientId == Coverage && route.Channel == NotificationChannel.Inbox && route.Mandatory && route.CoveredPersonId == Primary);
        Assert.Contains(routes, route => route.RecipientId == Coverage && route.Channel == NotificationChannel.Email && route.Mandatory && route.CoveredPersonId == Primary);
        Assert.Contains(routes, route => route.RecipientId == Personal && route.Channel == NotificationChannel.Inbox && !route.Mandatory);
        Assert.DoesNotContain(routes, route => route.RecipientId == Personal && route.Channel == NotificationChannel.Email);
    }

    [Fact]
    public void SourceLinkCarriesExactReauthorizationAndNotificationSurfaceCannotAcknowledgeAlarm()
    {
        var candidate = Candidate();

        Assert.Equal($"/events/{candidate.EventId.Value}", candidate.SourceLink.Route);
        Assert.Equal(
            ["events.dispatcher.read", $"runtime.point.p{PointId.Value:N}.read"],
            candidate.SourceLink.RequiredPermissions.Select(permission => permission.Value));
        Assert.DoesNotContain(
            typeof(NotificationService).Assembly.GetExportedTypes().SelectMany(type => type.GetMethods()),
            method => method.Name.Contains("Acknowledge", StringComparison.OrdinalIgnoreCase));
    }

    private static NotificationSubscription Subscription(
        PersonId personId,
        IReadOnlyCollection<NotificationChannel> channels) => new(
        NotificationSubscriptionId.New(),
        personId,
        ScopeId,
        StateVersion.Initial,
        AlarmPriority.Low,
        [PointId],
        channels,
        true);

    private static NotificationCandidate Candidate() => NotificationCandidate.FromOperationalEvent(
        new OperationalEventRecord(
            OperationalEventId.New(),
            new EventJournalPosition(1),
            ScopeId,
            PointId,
            AlarmOccurrenceId.New(),
            StateVersion.Initial,
            AlarmPriority.High,
            OperationalEventKind.AlarmRaised,
            Now,
            Now),
        "High alarm");
}
