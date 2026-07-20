using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Notifications;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class NotificationAcceptanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 18, 0, 0, TimeSpan.Zero);
    private static readonly RuntimeScopeId ScopeId = RuntimeScopeId.From(
        Guid.Parse("fa000000-0000-0000-0000-000000000001"));
    private static readonly SourceId SourceId = Dispatcher.Core.SourceId.From(
        Guid.Parse("fa000000-0000-0000-0000-000000000002"));
    private static readonly PointId PointId = Dispatcher.Semantics.PointId.From(
        Guid.Parse("fa000000-0000-0000-0000-000000000003"));
    private static readonly PersonId Primary = PersonId.From(
        Guid.Parse("fa000000-0000-0000-0000-000000000004"));
    private static readonly PersonId Coverage = PersonId.From(
        Guid.Parse("fa000000-0000-0000-0000-000000000005"));
    private readonly PostgreSqlClusterFixture cluster;

    public NotificationAcceptanceTests(PostgreSqlClusterFixture cluster) => this.cluster = cluster;

    [Fact]
    public async Task MandatoryCoverageInboxRestartAndSourceAuthorizationRemainIndependentFromAlarmAck()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            AlarmMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            EventMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            NotificationMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        var clock = new FixedClock();
        var alarmStore = new AlarmStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        Assert.True((await alarmStore.ActivateDefinitionSetAsync(new AlarmDefinitionSet(
            ScopeId,
            RevisionNumber.Initial,
            [new AlarmDefinition(
                AlarmDefinitionId.New(),
                PointId,
                "High value",
                AlarmThresholdDirection.High,
                10,
                0,
                TimeSpan.Zero,
                TimeSpan.Zero,
                priority: AlarmPriority.High)]))).IsSuccess);
        var occurrence = await RaiseAlarmAsync(alarmStore, clock);
        Assert.Equal(AlarmAcknowledgementState.Unacknowledged, occurrence.Acknowledgement.State);
        var eventStore = new EventStore(
            dataSource,
            PostgreSqlClusterFixture.OwnerARole,
            clock,
            new EventDispatcherLimits(100, 100, 50));
        var operationalEvent = (await eventStore.AcceptAlarmOccurrenceAsync(occurrence)).Value.Event;

        var notificationStore = new NotificationStore(
            dataSource,
            PostgreSqlClusterFixture.OwnerBRole,
            clock);
        var notifications = new NotificationService(notificationStore, clock);
        var administrator = Session(NotificationPermissions.ManagePolicy(ScopeId));
        Assert.True((await notifications.PublishPolicyAsync(
            administrator,
            new NotificationPolicy(
                NotificationPolicyId.New(),
                ScopeId,
                RevisionNumber.Initial,
                [new MandatoryNotificationRule(
                    NotificationRuleId.New(),
                    AlarmPriority.High,
                    [Primary],
                    [NotificationChannel.Inbox])]),
            null)).IsSuccess);
        var personalSession = Session(
            NotificationPermissions.Preferences,
            NotificationPermissions.Subscribe,
            NotificationPermissions.InboxRead);
        var personal = new NotificationUserContext(personalSession, Primary);
        Assert.True((await notifications.SaveSettingsAsync(
            personal,
            new PersonalNotificationSettings(
                Primary,
                StateVersion.Initial,
                null,
                [],
                new NotificationAbsence(Now.AddHours(-1), Now.AddHours(1), Coverage),
                [new NotificationChannelPreference(NotificationChannel.Inbox, false)]),
            null)).IsSuccess);
        Assert.True((await notifications.SaveSubscriptionAsync(
            personal,
            new NotificationSubscription(
                NotificationSubscriptionId.New(),
                Primary,
                ScopeId,
                StateVersion.Initial,
                AlarmPriority.Low,
                [PointId],
                [NotificationChannel.Inbox],
                true),
            null)).IsSuccess);

        var routed = await notifications.RouteEventAsync(operationalEvent, "High value alarm");

        Assert.True(routed.IsSuccess);
        Assert.Equal(2, routed.Value.InboxItems.Count);
        Assert.All(routed.Value.Routes, route => Assert.True(route.Mandatory));
        var primaryItem = routed.Value.InboxItems.Single(item => item.RecipientId == Primary);
        var marked = await notifications.MarkReadAsync(personal, primaryItem.ItemId, primaryItem.Version);
        Assert.NotNull(marked.Value.ReadAt);
        Assert.Equal(
            AlarmAcknowledgementState.Unacknowledged,
            Assert.Single(await alarmStore.ReadOccurrencesAsync(ScopeId)).Acknowledgement.State);

        var denied = await notifications.OpenSourceLinkAsync(personal, primaryItem.ItemId);
        Assert.Equal("permission.denied", denied.Error?.Code.Value);
        var authorizedContext = new NotificationUserContext(
            Session(
                NotificationPermissions.InboxRead,
                PermissionCode.From("events.dispatcher.read"),
                PermissionCode.From($"runtime.point.p{PointId.Value:N}.read")),
            Primary);
        var link = await notifications.OpenSourceLinkAsync(authorizedContext, primaryItem.ItemId);
        Assert.True(link.IsSuccess);
        Assert.Equal(operationalEvent.EventId, link.Value.EventId);

        var replay = await notifications.RouteEventAsync(operationalEvent, "High value alarm");
        Assert.True(replay.IsSuccess);
        Assert.Equal(
            routed.Value.InboxItems.OrderBy(item => item.RecipientId.Value).Select(item => item.ItemId),
            replay.Value.InboxItems.OrderBy(item => item.RecipientId.Value).Select(item => item.ItemId));

        var restarted = new NotificationService(
            new NotificationStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock),
            clock);
        var restored = await restarted.ReadInboxAsync(authorizedContext);
        Assert.NotNull(Assert.Single(restored.Value).ReadAt);
    }

    private static async Task<AlarmOccurrenceSnapshot> RaiseAlarmAsync(AlarmStore store, FixedClock clock)
    {
        var binding = new SourceBinding(
            ScopeId,
            SourceId,
            SourceBindingGeneration.From(1),
            SourceSessionGeneration.From(1));
        var runtime = new CoreRuntime(ScopeId, clock, clock, new RuntimeCurrentLimits(4, 8));
        Assert.True(runtime.ActivateBinding(binding).IsSuccess);
        var observation = new SourceObservation(
            ScopeId,
            SourceId,
            PointId,
            new OwnerPosition<SourceObservation>(1),
            TypedValue.From(42L),
            Unit.FromSymbol("kW"),
            DataQuality.Good,
            Freshness.Fresh,
            SourceTimestamp.FromUtc(Now));
        var cut = RuntimeCut.Normalize(binding, 1, [observation]).Value;
        var acceptance = runtime.Apply(cut).Value;
        var evaluated = await new AlarmEvaluator(ScopeId, RevisionNumber.Initial, store)
            .EvaluatePostRuntimeCutAsync(acceptance, runtime.GetSnapshot());
        return Assert.Single(evaluated.Value.Occurrences);
    }

    private static SessionSnapshot Session(params PermissionCode[] permissions) => new(
        SessionId.New(),
        SubjectId.New(),
        PrincipalKind.User,
        Now.AddMinutes(-1),
        Now.AddHours(1),
        new EffectivePermissions(permissions));

    private sealed class FixedClock : IWallClock, IMonotonicClock
    {
        public DateTimeOffset GetUtcNow() => Now;
        public MonotonicTimestamp GetTimestamp() => new(0);
        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) => TimeSpan.Zero;
    }
}
