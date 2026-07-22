using System.Text;
using Dispatcher.Identity;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class IdentityProductionAuthenticationTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly PermissionCode InitialPermission = PermissionCode.From("runtime.current.read");
    private static readonly PermissionCode ChangedPermission = PermissionCode.From("workspace.home.read");
    private readonly PostgreSqlClusterFixture cluster;

    public IdentityProductionAuthenticationTests(PostgreSqlClusterFixture cluster) => this.cluster = cluster;

    [Fact]
    public async Task ProductionIdentityLifecycleIsDurableInvalidatesAccessAndProtectsFinalAdministrator()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            IdentityMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        var clock = new MutableClock(Start);
        var policy = new IdentitySecurityPolicy(
            passwordIterations: 10_000,
            minimumPasswordLength: 12,
            maximumPasswordLength: 128,
            maximumFailedAttempts: 3,
            lockoutDuration: TimeSpan.FromMinutes(5),
            accessLifetime: TimeSpan.FromMinutes(10),
            refreshLifetime: TimeSpan.FromHours(1));
        var store = new IdentityStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock, policy);
        var administratorId = IdentityAccountId.New();
        var administratorRoleId = IdentityRoleId.New();
        const string administratorPassword = "Correct-Horse-35!";

        var bootstrap = await store.BootstrapAdministratorAsync(new BootstrapLocalAdministrator(
            administratorId,
            SubjectId.New(),
            null,
            administratorRoleId,
            "dispatcher-admin",
            administratorPassword));
        Assert.True(bootstrap.IsSuccess);
        Assert.Equal("identity.bootstrap_closed", (await store.BootstrapAdministratorAsync(new BootstrapLocalAdministrator(
            IdentityAccountId.New(), SubjectId.New(), null, IdentityRoleId.New(), "second-admin", administratorPassword))).Error?.Code.Value);

        var unknown = await store.LoginAsync(new LocalLoginRequest("missing-account", "Incorrect-Secret!"));
        var incorrect = await store.LoginAsync(new LocalLoginRequest("dispatcher-admin", "Incorrect-Secret!"));
        Assert.Equal(unknown.Error, incorrect.Error);
        Assert.Equal("identity.login_invalid", unknown.Error?.Code.Value);

        var administratorLogin = await store.LoginAsync(new LocalLoginRequest("dispatcher-admin", administratorPassword));
        Assert.True(administratorLogin.IsSuccess);
        await AssertOnlyHashesArePersistedAsync(dataSource, administratorPassword, administratorLogin.Value);

        var restarted = new IdentityStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock, policy);
        var recoveredAdministrator = await restarted.ResolveAccessAsync(Present(administratorLogin.Value.AccessToken));
        Assert.True(recoveredAdministrator.IsSuccess);
        var administratorMutation = SessionAuthorization.AuthorizeMutation(
            recoveredAdministrator.Value, IdentityPermissions.Administer, clock).Value;
        var administratorAccess = SessionAuthorization.AuthorizeAccess(
            recoveredAdministrator.Value, IdentityPermissions.Administer, clock).Value;
        var diagnosticsAccess = SessionAuthorization.AuthorizeAccess(
            recoveredAdministrator.Value, IdentityPermissions.ReadDiagnostics, clock).Value;

        var parentScopeId = IdentityScopeId.New();
        var childScopeId = IdentityScopeId.New();
        var roleId = IdentityRoleId.New();
        var groupId = IdentityGroupId.New();
        var userId = IdentityAccountId.New();
        Assert.True((await restarted.CreateScopeAsync(administratorMutation,
            new CreateIdentityScope(parentScopeId, "Operations"))).IsSuccess);
        Assert.True((await restarted.CreateScopeAsync(administratorMutation,
            new CreateIdentityScope(childScopeId, "Control room", parentScopeId))).IsSuccess);
        Assert.True((await restarted.CreateRoleAsync(administratorMutation,
            new CreateIdentityRole(roleId, "Operators", [new(InitialPermission, childScopeId)]))).IsSuccess);
        Assert.True((await restarted.CreateGroupAsync(administratorMutation,
            new CreateIdentityGroup(groupId, "Day shift"))).IsSuccess);
        Assert.True((await restarted.CreateAccountAsync(administratorMutation,
            new CreateIdentityAccount(userId, SubjectId.New(), null, childScopeId, "operator", "Operator-Secret-35!"))).IsSuccess);
        Assert.True((await restarted.AssignRoleToGroupAsync(administratorMutation, groupId, roleId)).IsSuccess);
        Assert.True((await restarted.AddGroupMemberAsync(administratorMutation, groupId, userId)).IsSuccess);

        var userLogin = await restarted.LoginAsync(new LocalLoginRequest("operator", "Operator-Secret-35!"));
        Assert.True(userLogin.IsSuccess);
        Assert.True(userLogin.Value.Session.Permissions.Allows(InitialPermission));
        Assert.False(userLogin.Value.Session.Permissions.Allows(IdentityPermissions.Administer));
        Assert.Equal("permission.denied", SessionAuthorization.AuthorizeMutation(
            userLogin.Value.Session, IdentityPermissions.Administer, clock).Error?.Code.Value);
        var nonAdministratorMutation = SessionAuthorization.AuthorizeMutation(
            userLogin.Value.Session, InitialPermission, clock).Value;
        Assert.Equal("permission.denied", (await restarted.CreateRoleAsync(nonAdministratorMutation,
            new CreateIdentityRole(IdentityRoleId.New(), "Forbidden", [new(ChangedPermission, null)]))).Error?.Code.Value);

        Assert.True((await restarted.SetGlobalSettingAsync(administratorMutation, "ui.theme", "global")).IsSuccess);
        Assert.True((await restarted.SetScopeSettingAsync(administratorMutation, parentScopeId, "ui.theme", "parent")).IsSuccess);
        Assert.True((await restarted.SetScopeSettingAsync(administratorMutation, childScopeId, "ui.theme", "child")).IsSuccess);
        Assert.Equal("child", (await restarted.ResolveSettingAsync(userLogin.Value.Session, "ui.theme")).Value.Value);
        Assert.True((await restarted.SetGroupSettingAsync(administratorMutation, groupId, "ui.theme", "group")).IsSuccess);
        Assert.Equal("group", (await restarted.ResolveSettingAsync(userLogin.Value.Session, "ui.theme")).Value.Value);
        Assert.True((await restarted.SetAccountSettingAsync(administratorMutation, userId, "ui.theme", "account")).IsSuccess);
        var accountSetting = await restarted.ResolveSettingAsync(userLogin.Value.Session, "ui.theme");
        Assert.Equal("account", accountSetting.Value.Value);
        Assert.Equal("Account", accountSetting.Value.Source);

        var nextGrants = new IdentityPermissionGrant[] { new(ChangedPermission, childScopeId) };
        var preview = await restarted.PreviewRolePermissionsAsync(administratorAccess, roleId, nextGrants);
        Assert.Equal([ChangedPermission], preview.Value.Added);
        Assert.Equal([InitialPermission], preview.Value.Removed);
        Assert.Equal(1, preview.Value.AffectedAccounts);
        Assert.Equal(1, preview.Value.ActiveSessions);
        var changed = await restarted.UpdateRolePermissionsAsync(
            administratorMutation, roleId, nextGrants, StateVersion.Initial, preview.Value.Fingerprint);
        Assert.True(changed.IsSuccess);
        Assert.Equal("identity.session_invalid", (await restarted.ResolveAccessAsync(Present(userLogin.Value.AccessToken))).Error?.Code.Value);
        var changedUserLogin = await restarted.LoginAsync(new LocalLoginRequest("operator", "Operator-Secret-35!"));
        Assert.True(changedUserLogin.Value.Session.Permissions.Allows(ChangedPermission));
        Assert.False(changedUserLogin.Value.Session.Permissions.Allows(InitialPermission));

        Assert.True((await restarted.SetAccountPermissionOverrideAsync(
            administratorMutation, userId, ChangedPermission, false)).IsSuccess);
        Assert.Equal("identity.session_invalid", (await restarted.ResolveAccessAsync(
            Present(changedUserLogin.Value.AccessToken))).Error?.Code.Value);
        var deniedUserLogin = await restarted.LoginAsync(new LocalLoginRequest("operator", "Operator-Secret-35!"));
        Assert.False(deniedUserLogin.Value.Session.Permissions.Allows(ChangedPermission));
        Assert.True((await restarted.SetAccountPermissionOverrideAsync(
            administratorMutation, userId, ChangedPermission, true)).IsSuccess);
        changedUserLogin = await restarted.LoginAsync(new LocalLoginRequest("operator", "Operator-Secret-35!"));
        Assert.True(changedUserLogin.Value.Session.Permissions.Allows(ChangedPermission));

        var administratorPreview = await restarted.PreviewRolePermissionsAsync(
            administratorAccess,
            administratorRoleId,
            [new IdentityPermissionGrant(IdentityPermissions.ReadDiagnostics, null)]);
        var removeFinalAdministrator = await restarted.UpdateRolePermissionsAsync(
            administratorMutation,
            administratorRoleId,
            [new IdentityPermissionGrant(IdentityPermissions.ReadDiagnostics, null)],
            StateVersion.Initial,
            administratorPreview.Value.Fingerprint);
        Assert.Equal("identity.last_administrator", removeFinalAdministrator.Error?.Code.Value);
        Assert.Equal("identity.last_administrator", (await restarted.SetAccountEnabledAsync(
            administratorMutation, administratorId, false, StateVersion.Initial)).Error?.Code.Value);

        var diagnostic = await restarted.ReadIntegrationDiagnosticAsync(diagnosticsAccess);
        Assert.Equal(IdentityIntegrationKind.LocalDispatcherAuthentication, diagnostic.Value.Kind);
        Assert.Equal(IdentityIntegrationStatus.Healthy, diagnostic.Value.Status);
        Assert.True(diagnostic.Value.SecretConfigured);
        Assert.DoesNotContain("password", diagnostic.Value.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(administratorPassword, diagnostic.Value.Summary, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal("identity.session_invalid", (await restarted.ResolveAccessAsync(
            Present(changedUserLogin.Value.AccessToken))).Error?.Code.Value);
        var refreshed = await restarted.RefreshAsync(new RefreshProductionSession(changedUserLogin.Value.RefreshToken));
        Assert.True(refreshed.IsSuccess);
        Assert.Equal("identity.session_invalid", (await restarted.ResolveAccessAsync(
            Present(changedUserLogin.Value.AccessToken))).Error?.Code.Value);
        Assert.True((await restarted.ResolveAccessAsync(Present(refreshed.Value.AccessToken))).IsSuccess);
        Assert.True((await restarted.RevokeAsync(refreshed.Value.AccessToken)).IsSuccess);
        Assert.Equal("identity.session_invalid", (await restarted.ResolveAccessAsync(
            Present(refreshed.Value.AccessToken))).Error?.Code.Value);
    }

    private static ProductionAccessPresentation Present(string token) =>
        new(ProductionAccessPresentation.RequiredScheme, token);

    private static async Task AssertOnlyHashesArePersistedAsync(
        NpgsqlDataSource dataSource,
        string password,
        ProductionSessionIssue issue)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT account.password_hash, session.access_token_hash, session.refresh_token_hash
            FROM identity.account AS account
            JOIN identity.production_session AS session USING (account_id)
            WHERE session.session_id=@session;
            """, connection);
        command.Parameters.AddWithValue("session", issue.Session.Id.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(32, reader.GetFieldValue<byte[]>(0).Length);
        Assert.Equal(32, reader.GetFieldValue<byte[]>(1).Length);
        Assert.Equal(32, reader.GetFieldValue<byte[]>(2).Length);
        Assert.NotEqual(Encoding.UTF8.GetBytes(password), reader.GetFieldValue<byte[]>(0));
        Assert.NotEqual(Encoding.UTF8.GetBytes(issue.AccessToken), reader.GetFieldValue<byte[]>(1));
        Assert.NotEqual(Encoding.UTF8.GetBytes(issue.RefreshToken), reader.GetFieldValue<byte[]>(2));
    }

    private sealed class MutableClock(DateTimeOffset now) : IWallClock
    {
        private DateTimeOffset current = now;
        public DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current = current.Add(duration);
    }
}
