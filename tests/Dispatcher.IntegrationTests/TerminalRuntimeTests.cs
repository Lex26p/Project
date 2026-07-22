using Dispatcher.Core;
using Dispatcher.Dashboards;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Server;
using Dispatcher.Terminals;
using Dispatcher.Workspace;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class TerminalRuntimeTests(PostgreSqlClusterFixture cluster)
{
    [Fact]
    public async Task AssignedRuntimeHeartbeatPinAttributionOfflineAndWallboardDenyAreEnforced()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, DashboardMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, TerminalMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        var clock = new MutableClock(new DateTimeOffset(2026, 7, 22, 16, 0, 0, TimeSpan.Zero));
        var dashboardStore = new DashboardStore(dataSource, PostgreSqlClusterFixture.OwnerARole, clock);
        var dashboardId = DashboardId.New();
        var allowedPermission = PermissionCode.From("runtime.point.allowed.read");
        var hiddenPermission = PermissionCode.From("runtime.point.hidden.read");
        await dashboardStore.PublishAsync(Revision(dashboardId, clock.GetUtcNow(), allowedPermission, hiddenPermission));

        var terminals = new TerminalStore(
            dataSource, PostgreSqlClusterFixture.OwnerBRole, clock,
            new TerminalEnrollmentPolicy(TimeSpan.FromMinutes(5), TimeSpan.FromHours(4)),
            new TerminalPinPolicy(10_000, 4, 8, TimeSpan.FromMinutes(2)));
        var session = Session(clock, TerminalPermissions.Manage, TerminalPermissions.ApproveEnrollment);
        var manage = SessionAuthorization.AuthorizeMutation(session, TerminalPermissions.Manage, clock).Value;
        var approve = SessionAuthorization.AuthorizeMutation(session, TerminalPermissions.ApproveEnrollment, clock).Value;
        var profileId = TerminalProfileId.New();
        var profile = (await terminals.CreateProfileAsync(manage, new CreateTerminalProfile(profileId, "Kiosk profile"))).Value;
        profile = (await terminals.AssignContentAsync(manage, new AssignTerminalContent(
            profileId, new TerminalContentAssignment(TerminalContentKind.Dashboard, dashboardId.Value), profile.Version))).Value;
        profile = (await terminals.ConfigureRuntimeAsync(manage, new ConfigureTerminalRuntime(
            profileId,
            new TerminalRuntimePolicy(
                TerminalExperience.Kiosk, TerminalOfflineMode.ReadOnlyLastSynchronized,
                TerminalEmployeeReauthentication.NotRequired,
                [RuntimePermissions.ReadCurrent, allowedPermission]),
            profile.Version))).Value;
        var enrolled = await EnrollAsync(terminals, approve);
        var assigned = (await terminals.AssignProfileAsync(manage, new AssignTerminalProfile(
            enrolled.Issue.TerminalId, profileId, StateVersion.From(2)))).Value;
        var authenticated = (await terminals.AuthenticateAsync(Header(enrolled.Issue.Credential))).Value;
        var runtime = new AuthorizedTerminalRuntimeService(
            terminals, new AuthorizedDashboardService(dashboardStore, clock), clock);
        var synchronized = (await runtime.SynchronizeAsync(authenticated, CancellationToken.None)).Value;
        Assert.Equal(dashboardId.Value, synchronized.Dashboard.DashboardId);
        Assert.Single(Assert.Single(synchronized.Dashboard.Windows).Bindings);
        Assert.DoesNotContain(
            synchronized.Dashboard.Windows.SelectMany(value => value.Bindings),
            value => value.PointId == HiddenPointId.Value);
        var heartbeat = (await runtime.HeartbeatAsync(
            authenticated, profile.Version, CancellationToken.None)).Value;
        Assert.False(heartbeat.ResyncRequired);

        profile = (await terminals.ConfigureRuntimeAsync(manage, new ConfigureTerminalRuntime(
            profileId,
            profile.RuntimePolicy with { EmployeeReauthentication = TerminalEmployeeReauthentication.Required },
            profile.Version))).Value;
        authenticated = (await terminals.AuthenticateAsync(Header(enrolled.Issue.Credential))).Value;
        heartbeat = (await runtime.HeartbeatAsync(
            authenticated, StateVersion.From(profile.Version.Value - 1), CancellationToken.None)).Value;
        Assert.True(heartbeat.ResyncRequired);
        var personId = PersonId.From(Guid.Parse("fa000000-0000-0000-0000-000000000004"));
        Assert.True((await terminals.SetEmployeePinAsync(
            manage, new SetTerminalEmployeePin(profileId, personId, "4826"))).IsSuccess);
        Assert.Equal("terminal.reauthentication_required",
            (await terminals.AuthorizeInteractionAsync(
                authenticated, Guid.NewGuid(), "open-detail", null)).Error?.Code.Value);
        Assert.Equal("terminal.pin_invalid",
            (await terminals.ReauthenticateEmployeeAsync(authenticated, personId, "0000")).Error?.Code.Value);
        var reauthentication = (await terminals.ReauthenticateEmployeeAsync(authenticated, personId, "4826")).Value;
        var attributed = (await terminals.AuthorizeInteractionAsync(
            authenticated, Guid.NewGuid(), "open-detail", new(reauthentication.Token))).Value;
        Assert.Equal(personId, attributed.PersonId);

        profile = (await terminals.ConfigureRuntimeAsync(manage, new ConfigureTerminalRuntime(
            profileId,
            profile.RuntimePolicy with
            {
                Experience = TerminalExperience.Wallboard,
                EmployeeReauthentication = TerminalEmployeeReauthentication.NotRequired,
            }, profile.Version))).Value;
        authenticated = (await terminals.AuthenticateAsync(Header(enrolled.Issue.Credential))).Value;
        var terminalOnly = (await terminals.AuthorizeInteractionAsync(
            authenticated, Guid.NewGuid(), "refresh-view", null)).Value;
        Assert.Null(terminalOnly.PersonId);
        Assert.Equal(enrolled.Issue.TerminalId, terminalOnly.TerminalId);
        Assert.Equal("terminal.wallboard_command_denied",
            TerminalCommandAdmission.Evaluate(profile.RuntimePolicy, online: true).Error?.Code.Value);
        Assert.Equal("terminal.offline_command_not_queued",
            TerminalCommandAdmission.Evaluate(profile.RuntimePolicy, online: false).Error?.Code.Value);
        Assert.Equal(assigned.TerminalId, authenticated.Terminal.TerminalId);
    }

    private static readonly PointId HiddenPointId = PointId.From(
        Guid.Parse("fa000000-0000-0000-0000-000000000002"));

    private static DashboardRevision Revision(
        DashboardId id, DateTimeOffset now, PermissionCode allowedPermission, PermissionCode hiddenPermission)
    {
        var allowedBinding = DashboardBindingId.New();
        var hiddenBinding = DashboardBindingId.New();
        return new DashboardRevision(
            id, DashboardRevisionId.New(), 1, "Assigned operations", null,
            [new DashboardWindow(
                DashboardWindowId.New(), "Operations",
                [
                    new Widget(WidgetId.New(), "value", "Allowed", [allowedBinding]),
                    new Widget(WidgetId.New(), "value", "Hidden", [hiddenBinding]),
                ],
                [
                    new DashboardBinding(allowedBinding, DashboardBindingSource.Current,
                        RuntimeScopeId.From(Guid.Parse("fa000000-0000-0000-0000-000000000001")),
                        PointId.From(Guid.Parse("fa000000-0000-0000-0000-000000000003")), allowedPermission),
                    new DashboardBinding(hiddenBinding, DashboardBindingSource.Current,
                        RuntimeScopeId.From(Guid.Parse("fa000000-0000-0000-0000-000000000001")),
                        HiddenPointId, hiddenPermission),
                ])],
            [
                new DashboardDependency(allowedBinding, "allowed", "v1"),
                new DashboardDependency(hiddenBinding, "hidden", "v1"),
            ], now);
    }

    private static async Task<(TerminalEnrollmentChallenge Challenge, TerminalCredentialIssue Issue)> EnrollAsync(
        TerminalStore store, AuthorizedMutation approval)
    {
        var request = new InitiateTerminalEnrollment(
            TerminalEnrollmentId.New(), TerminalId.New(), "S34 terminal");
        var challenge = (await store.InitiateEnrollmentAsync(request)).Value;
        Assert.True((await store.ApproveEnrollmentAsync(approval, request.EnrollmentId)).IsSuccess);
        return (challenge, (await store.ExchangeChallengeAsync(request.EnrollmentId, challenge.Challenge)).Value);
    }

    private static TerminalCredentialPresentation Header(string credential) =>
        new(TerminalCredentialPresentation.RequiredScheme, credential);

    private static SessionSnapshot Session(MutableClock clock, params PermissionCode[] permissions) => new(
        SessionId.New(), SubjectId.New(), PrincipalKind.User, clock.GetUtcNow().AddMinutes(-1),
        clock.GetUtcNow().AddHours(2), new EffectivePermissions(permissions));

    private sealed class MutableClock(DateTimeOffset now) : IWallClock
    {
        public DateTimeOffset GetUtcNow() => now;
    }
}
