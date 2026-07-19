using Dispatcher.Platform;
using Dispatcher.Semantics;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class SessionSecurityTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 1, 0, 0, TimeSpan.Zero);
    private static readonly PermissionCode WritePermission = PermissionCode.From("equipment.write");

    [Theory]
    [InlineData(PrincipalKind.User)]
    [InlineData(PrincipalKind.Device)]
    [InlineData(PrincipalKind.Workload)]
    public void ExplicitPermissionAuthorizesEveryPrincipalKind(PrincipalKind principalKind)
    {
        var session = CreateSession(principalKind, [WritePermission]);

        var result = SessionAuthorization.AuthorizeMutation(session, WritePermission, new FixedClock(Now));

        Assert.True(result.IsSuccess);
        Assert.Equal(principalKind, result.Value.Session.PrincipalKind);
    }

    [Fact]
    public void AnonymousAndAdminNamedPermissionDoNotBypassExactCheck()
    {
        var clock = new FixedClock(Now);
        var anonymous = SessionAuthorization.AuthorizeMutation(null, WritePermission, clock);
        var adminNamedSession = CreateSession(PrincipalKind.User, [PermissionCode.From("platform.admin")]);
        var adminNamed = SessionAuthorization.AuthorizeMutation(adminNamedSession, WritePermission, clock);

        Assert.Equal("session.anonymous", anonymous.Error?.Code.Value);
        Assert.Equal("permission.denied", adminNamed.Error?.Code.Value);
    }

    [Fact]
    public void RevokedAndExpiredSessionsCannotMutate()
    {
        var clock = new FixedClock(Now);
        var active = CreateSession(PrincipalKind.User, [WritePermission]);
        var revoked = active.Revoke(Now);
        var expired = new SessionSnapshot(
            SessionId.New(),
            SubjectId.New(),
            PrincipalKind.User,
            Now.AddHours(-2),
            Now,
            new EffectivePermissions([WritePermission]));

        Assert.Equal(
            "session.revoked",
            SessionAuthorization.AuthorizeMutation(revoked, WritePermission, clock).Error?.Code.Value);
        Assert.Equal(
            "session.expired",
            SessionAuthorization.AuthorizeMutation(expired, WritePermission, clock).Error?.Code.Value);
    }

    [Fact]
    public void ExplicitDenialOverridesGrant()
    {
        var permissions = new EffectivePermissions([WritePermission], [WritePermission]);
        var session = new SessionSnapshot(
            SessionId.New(),
            SubjectId.New(),
            PrincipalKind.User,
            Now.AddMinutes(-1),
            Now.AddHours(1),
            permissions);

        var result = SessionAuthorization.AuthorizeMutation(session, WritePermission, new FixedClock(Now));

        Assert.Equal("permission.denied", result.Error?.Code.Value);
    }

    [Fact]
    public void TestIdentityIsEnvironmentGatedAndLeastPrivilege()
    {
        var clock = new FixedClock(Now);
        var production = TestIdentityIssuer.Issue(
            new TestIdentityOptions(true, "Production", PrincipalKind.User, [WritePermission], TimeSpan.FromHours(1)),
            clock);
        var disabled = TestIdentityIssuer.Issue(
            new TestIdentityOptions(false, "Development", PrincipalKind.User, [WritePermission], TimeSpan.FromHours(1)),
            clock);
        var development = TestIdentityIssuer.Issue(
            new TestIdentityOptions(true, "Development", PrincipalKind.User, [WritePermission], TimeSpan.FromHours(1)),
            clock);

        Assert.Equal("test_identity.disabled", production.Error?.Code.Value);
        Assert.Equal("test_identity.disabled", disabled.Error?.Code.Value);
        Assert.True(development.IsSuccess);
        Assert.True(development.Value.Permissions.Allows(WritePermission));
        Assert.False(development.Value.Permissions.Allows(PermissionCode.From("platform.admin")));
    }

    private static SessionSnapshot CreateSession(
        PrincipalKind principalKind,
        IReadOnlyCollection<PermissionCode> permissions) => new(
        SessionId.New(),
        SubjectId.New(),
        principalKind,
        Now.AddMinutes(-1),
        Now.AddHours(1),
        new EffectivePermissions(permissions));

    private sealed class FixedClock : IWallClock
    {
        private readonly DateTimeOffset now;

        public FixedClock(DateTimeOffset now)
        {
            this.now = now;
        }

        public DateTimeOffset GetUtcNow() => now;
    }
}
