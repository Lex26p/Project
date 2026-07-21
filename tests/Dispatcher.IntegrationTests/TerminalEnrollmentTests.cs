using System.Text;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Terminals;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class TerminalEnrollmentTests
{
    private readonly PostgreSqlClusterFixture cluster;
    public TerminalEnrollmentTests(PostgreSqlClusterFixture cluster) => this.cluster = cluster;

    [Fact]
    public async Task EnrollmentIdentityFleetAndCredentialRacesCloseIg12()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, TerminalMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        var clock = new MutableClock(new DateTimeOffset(2026, 7, 22, 14, 0, 0, TimeSpan.Zero));
        var policy = new TerminalEnrollmentPolicy(TimeSpan.FromMinutes(5), TimeSpan.FromHours(1));
        var store = new TerminalStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock, policy);
        var session = Session(clock, TerminalPermissions.Manage, TerminalPermissions.ApproveEnrollment, TerminalPermissions.Read);
        var manage = SessionAuthorization.AuthorizeMutation(session, TerminalPermissions.Manage, clock).Value;
        var approve = SessionAuthorization.AuthorizeMutation(session, TerminalPermissions.ApproveEnrollment, clock).Value;
        var read = SessionAuthorization.AuthorizeAccess(session, TerminalPermissions.Read, clock).Value;
        var profileId = TerminalProfileId.New();
        var profile = (await store.CreateProfileAsync(manage, new CreateTerminalProfile(profileId, "Shared wall profile"))).Value;
        var content = new TerminalContentAssignment(
            TerminalContentKind.Dashboard, Guid.Parse("ff000000-0000-0000-0000-000000000001"));
        profile = (await store.AssignContentAsync(
            manage, new AssignTerminalContent(profileId, content, profile.Version))).Value;

        var terminalA = await EnrollAsync(store, approve, "Terminal A");
        var terminalB = await EnrollAsync(store, approve, "Terminal B");
        Assert.NotEqual(terminalA.Issue.TerminalId, terminalB.Issue.TerminalId);
        Assert.NotEqual(terminalA.Issue.DeviceIdentityId, terminalB.Issue.DeviceIdentityId);
        var assignedA = (await store.AssignProfileAsync(manage, new AssignTerminalProfile(
            terminalA.Issue.TerminalId, profileId, StateVersion.From(2)))).Value;
        var assignedB = (await store.AssignProfileAsync(manage, new AssignTerminalProfile(
            terminalB.Issue.TerminalId, profileId, StateVersion.From(2)))).Value;

        var queryOnly = TerminalRequestIdentity.Resolve(
            null, new Dictionary<string, string?> { ["terminalCredential"] = terminalA.Issue.Credential });
        Assert.Null(queryOnly);
        Assert.Equal("terminal.authentication_required", (await store.AuthenticateAsync(queryOnly)).Error?.Code.Value);
        var authenticatedA = (await store.AuthenticateAsync(Header(terminalA.Issue.Credential))).Value;
        var authenticatedB = (await store.AuthenticateAsync(Header(terminalB.Issue.Credential))).Value;
        Assert.Equal(profileId, authenticatedA.Profile!.ProfileId);
        Assert.Equal(profileId, authenticatedB.Profile!.ProfileId);
        Assert.Equal(content, authenticatedA.Content);
        Assert.NotEqual(authenticatedA.DeviceIdentityId, authenticatedB.DeviceIdentityId);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(clock.GetUtcNow(), (await store.RecordPresenceAsync(authenticatedA)).Value);
        var fleet = (await store.ReadFleetAsync(read)).Value;
        Assert.Equal(2, fleet.Count);
        Assert.Equal(clock.GetUtcNow(), fleet.Single(value => value.TerminalId == terminalA.Issue.TerminalId).LastSeenAt);

        var restarted = new TerminalStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock, policy);
        Assert.Equal(terminalA.Issue.DeviceIdentityId,
            (await restarted.AuthenticateAsync(Header(terminalA.Issue.Credential))).Value.DeviceIdentityId);
        await AssertHashOnlyStorageAsync(dataSource, terminalA.Challenge.Challenge, terminalA.Issue.Credential);

        var raceRequest = new InitiateTerminalEnrollment(
            TerminalEnrollmentId.New(), TerminalId.New(), "Race terminal");
        var raceChallenge = (await store.InitiateEnrollmentAsync(raceRequest)).Value;
        Assert.True((await store.ApproveEnrollmentAsync(approve, raceRequest.EnrollmentId)).IsSuccess);
        var race = await Task.WhenAll(
            store.ExchangeChallengeAsync(raceRequest.EnrollmentId, raceChallenge.Challenge),
            restarted.ExchangeChallengeAsync(raceRequest.EnrollmentId, raceChallenge.Challenge));
        Assert.Single(race, value => value.IsSuccess);
        Assert.Single(race, value => value.Error?.Code.Value == "terminal.enrollment_replay");

        var expiringRequest = new InitiateTerminalEnrollment(
            TerminalEnrollmentId.New(), TerminalId.New(), "Expiring challenge");
        var expiringChallenge = (await store.InitiateEnrollmentAsync(expiringRequest)).Value;
        Assert.True((await store.ApproveEnrollmentAsync(approve, expiringRequest.EnrollmentId)).IsSuccess);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("terminal.enrollment_expired",
            (await store.ExchangeChallengeAsync(expiringRequest.EnrollmentId, expiringChallenge.Challenge)).Error?.Code.Value);

        var expiringCredential = await EnrollAsync(store, approve, "Credential expiry terminal");
        var beforeExpiry = (await store.AuthenticateAsync(Header(expiringCredential.Issue.Credential))).Value;

        var blocked = (await store.BlockAsync(manage, new ChangeTerminalState(
            assignedA.TerminalId, assignedA.Version))).Value;
        Assert.Equal(TerminalState.Blocked, blocked.State);
        Assert.Equal("terminal.not_active", (await store.AuthenticateAsync(Header(terminalA.Issue.Credential))).Error?.Code.Value);
        Assert.Equal("terminal.not_active", (await store.RecordPresenceAsync(authenticatedA)).Error?.Code.Value);

        var inFlightAuthentication = store.AuthenticateAsync(Header(terminalB.Issue.Credential));
        var revoke = store.RevokeAsync(manage, new ChangeTerminalState(assignedB.TerminalId, assignedB.Version));
        await Task.WhenAll(inFlightAuthentication, revoke);
        Assert.True((await revoke).IsSuccess);
        var afterRevoke = new TerminalStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock, policy);
        Assert.True((await afterRevoke.AuthenticateAsync(Header(terminalB.Issue.Credential))).IsFailure);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal("terminal.credential_expired",
            (await afterRevoke.AuthenticateAsync(Header(expiringCredential.Issue.Credential))).Error?.Code.Value);
        Assert.Equal("terminal.not_active", (await afterRevoke.RecordPresenceAsync(beforeExpiry)).Error?.Code.Value);
    }

    private static async Task<(TerminalEnrollmentChallenge Challenge, TerminalCredentialIssue Issue)> EnrollAsync(
        TerminalStore store, AuthorizedMutation approval, string label)
    {
        var request = new InitiateTerminalEnrollment(TerminalEnrollmentId.New(), TerminalId.New(), label);
        var challenge = (await store.InitiateEnrollmentAsync(request)).Value;
        Assert.Equal(TerminalEnrollmentState.Approved,
            (await store.ApproveEnrollmentAsync(approval, request.EnrollmentId)).Value);
        return (challenge, (await store.ExchangeChallengeAsync(request.EnrollmentId, challenge.Challenge)).Value);
    }

    private static TerminalCredentialPresentation Header(string credential) =>
        TerminalRequestIdentity.Resolve(
            $"{TerminalCredentialPresentation.RequiredScheme} {credential}",
            new Dictionary<string, string?>())!;

    private static async Task AssertHashOnlyStorageAsync(
        NpgsqlDataSource dataSource, string challenge, string credential)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT enrollment.challenge_hash, identity.credential_hash FROM terminals.enrollment AS enrollment JOIN terminals.device_identity AS identity USING (terminal_id) ORDER BY enrollment.created_at LIMIT 1;",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var challengeHash = reader.GetFieldValue<byte[]>(0);
        var credentialHash = reader.GetFieldValue<byte[]>(1);
        Assert.Equal(32, challengeHash.Length);
        Assert.Equal(32, credentialHash.Length);
        Assert.NotEqual(Encoding.UTF8.GetBytes(challenge), challengeHash);
        Assert.NotEqual(Encoding.UTF8.GetBytes(credential), credentialHash);
    }

    private static SessionSnapshot Session(MutableClock clock, params PermissionCode[] permissions) => new(
        SessionId.New(), SubjectId.New(), PrincipalKind.User, clock.GetUtcNow().AddMinutes(-1),
        clock.GetUtcNow().AddHours(3), new EffectivePermissions(permissions));

    private sealed class MutableClock(DateTimeOffset now) : IWallClock
    {
        private DateTimeOffset now = now;
        public DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
