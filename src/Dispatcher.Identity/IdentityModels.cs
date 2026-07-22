using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;

namespace Dispatcher.Identity;

public readonly record struct IdentityAccountId
{
    private readonly CanonicalId<IdentityAccountId> value;
    private IdentityAccountId(CanonicalId<IdentityAccountId> value) => this.value = value;
    public Guid Value => value.Value;
    public static IdentityAccountId New() => new(CanonicalId.New<IdentityAccountId>());
    public static IdentityAccountId From(Guid value) => new(CanonicalId.From<IdentityAccountId>(value));
}

public readonly record struct IdentityRoleId
{
    private readonly CanonicalId<IdentityRoleId> value;
    private IdentityRoleId(CanonicalId<IdentityRoleId> value) => this.value = value;
    public Guid Value => value.Value;
    public static IdentityRoleId New() => new(CanonicalId.New<IdentityRoleId>());
    public static IdentityRoleId From(Guid value) => new(CanonicalId.From<IdentityRoleId>(value));
}

public readonly record struct IdentityGroupId
{
    private readonly CanonicalId<IdentityGroupId> value;
    private IdentityGroupId(CanonicalId<IdentityGroupId> value) => this.value = value;
    public Guid Value => value.Value;
    public static IdentityGroupId New() => new(CanonicalId.New<IdentityGroupId>());
    public static IdentityGroupId From(Guid value) => new(CanonicalId.From<IdentityGroupId>(value));
}

public readonly record struct IdentityScopeId
{
    private readonly CanonicalId<IdentityScopeId> value;
    private IdentityScopeId(CanonicalId<IdentityScopeId> value) => this.value = value;
    public Guid Value => value.Value;
    public static IdentityScopeId New() => new(CanonicalId.New<IdentityScopeId>());
    public static IdentityScopeId From(Guid value) => new(CanonicalId.From<IdentityScopeId>(value));
}

public sealed record IdentitySecurityPolicy
{
    public IdentitySecurityPolicy(
        int passwordIterations, int minimumPasswordLength, int maximumPasswordLength,
        int maximumFailedAttempts, TimeSpan lockoutDuration,
        TimeSpan accessLifetime, TimeSpan refreshLifetime)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(passwordIterations);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumPasswordLength, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPasswordLength, minimumPasswordLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFailedAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lockoutDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(accessLifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(refreshLifetime, accessLifetime);
        PasswordIterations = passwordIterations;
        MinimumPasswordLength = minimumPasswordLength;
        MaximumPasswordLength = maximumPasswordLength;
        MaximumFailedAttempts = maximumFailedAttempts;
        LockoutDuration = lockoutDuration;
        AccessLifetime = accessLifetime;
        RefreshLifetime = refreshLifetime;
    }
    public int PasswordIterations { get; }
    public int MinimumPasswordLength { get; }
    public int MaximumPasswordLength { get; }
    public int MaximumFailedAttempts { get; }
    public TimeSpan LockoutDuration { get; }
    public TimeSpan AccessLifetime { get; }
    public TimeSpan RefreshLifetime { get; }
}

public sealed record IdentityAccountSnapshot(
    IdentityAccountId AccountId, SubjectId SubjectId, AccountId? WorkspaceAccountId,
    string UserName, bool Enabled, IdentityScopeId? PrimaryScopeId,
    StateVersion AuthorizationVersion, StateVersion Version, DateTimeOffset UpdatedAt);

public sealed record IdentityRoleSnapshot(
    IdentityRoleId RoleId, string Name, IReadOnlyList<IdentityPermissionGrant> Grants,
    StateVersion Version, DateTimeOffset UpdatedAt);
public sealed record IdentityPermissionGrant(PermissionCode Permission, IdentityScopeId? ScopeId);
public sealed record IdentityGroupSnapshot(IdentityGroupId GroupId, string Name, StateVersion Version);
public sealed record IdentityScopeSnapshot(IdentityScopeId ScopeId, string Name, IdentityScopeId? ParentScopeId);

public sealed record BootstrapLocalAdministrator(
    IdentityAccountId AccountId, SubjectId SubjectId, AccountId? WorkspaceAccountId,
    IdentityRoleId AdministratorRoleId, string UserName, string Password);
public sealed record CreateIdentityAccount(
    IdentityAccountId AccountId, SubjectId SubjectId, AccountId? WorkspaceAccountId,
    IdentityScopeId? PrimaryScopeId, string UserName, string Password);
public sealed record CreateIdentityRole(
    IdentityRoleId RoleId, string Name, IReadOnlyCollection<IdentityPermissionGrant> Grants);
public sealed record CreateIdentityGroup(IdentityGroupId GroupId, string Name);
public sealed record CreateIdentityScope(IdentityScopeId ScopeId, string Name, IdentityScopeId? ParentScopeId = null);

public sealed record LocalLoginRequest(string UserName, string Password);
public sealed record RefreshProductionSession(string RefreshToken);
public sealed record ProductionSessionIssue(
    IdentityAccountId AccountId, SessionSnapshot Session, string AccessToken, string RefreshToken,
    DateTimeOffset RefreshExpiresAt);
public sealed record ProductionAccessPresentation(string Scheme, string Token)
{
    public const string RequiredScheme = "Dispatcher-Session";
}

public sealed record RoleImpactPreview(
    IdentityRoleId RoleId, IReadOnlyList<PermissionCode> Added, IReadOnlyList<PermissionCode> Removed,
    int AffectedAccounts, int ActiveSessions, string Fingerprint);

public sealed record ResolvedIdentitySetting(string Key, string Value, string Source, Guid? SourceId);

public enum IdentityIntegrationKind { LocalDispatcherAuthentication = 1 }
public enum IdentityIntegrationStatus { Unknown = 1, Healthy = 2, Degraded = 3 }
public sealed record IdentityIntegrationDiagnostic(
    IdentityIntegrationKind Kind, IdentityIntegrationStatus Status, string Summary,
    bool SecretConfigured, DateTimeOffset CheckedAt);

public static class IdentityPermissions
{
    public static readonly PermissionCode Administer = PermissionCode.From("identity.administration.manage");
    public static readonly PermissionCode ReadDiagnostics = PermissionCode.From("identity.diagnostics.read");
}
