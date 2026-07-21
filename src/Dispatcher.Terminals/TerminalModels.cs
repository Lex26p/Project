using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Terminals;

public readonly record struct TerminalId
{
    private readonly CanonicalId<TerminalId> value;
    private TerminalId(CanonicalId<TerminalId> value) => this.value = value;
    public Guid Value => value.Value;
    public static TerminalId New() => new(CanonicalId.New<TerminalId>());
    public static TerminalId From(Guid value) => new(CanonicalId.From<TerminalId>(value));
}

public readonly record struct TerminalEnrollmentId
{
    private readonly CanonicalId<TerminalEnrollmentId> value;
    private TerminalEnrollmentId(CanonicalId<TerminalEnrollmentId> value) => this.value = value;
    public Guid Value => value.Value;
    public static TerminalEnrollmentId New() => new(CanonicalId.New<TerminalEnrollmentId>());
    public static TerminalEnrollmentId From(Guid value) => new(CanonicalId.From<TerminalEnrollmentId>(value));
}

public readonly record struct TerminalDeviceIdentityId
{
    private readonly CanonicalId<TerminalDeviceIdentityId> value;
    private TerminalDeviceIdentityId(CanonicalId<TerminalDeviceIdentityId> value) => this.value = value;
    public Guid Value => value.Value;
    public static TerminalDeviceIdentityId New() => new(CanonicalId.New<TerminalDeviceIdentityId>());
    public static TerminalDeviceIdentityId From(Guid value) => new(CanonicalId.From<TerminalDeviceIdentityId>(value));
}

public readonly record struct TerminalProfileId
{
    private readonly CanonicalId<TerminalProfileId> value;
    private TerminalProfileId(CanonicalId<TerminalProfileId> value) => this.value = value;
    public Guid Value => value.Value;
    public static TerminalProfileId New() => new(CanonicalId.New<TerminalProfileId>());
    public static TerminalProfileId From(Guid value) => new(CanonicalId.From<TerminalProfileId>(value));
}

public enum TerminalState { PendingEnrollment = 1, Active = 2, Blocked = 3, Revoked = 4 }
public enum TerminalEnrollmentState { PendingApproval = 1, Approved = 2, Consumed = 3, Expired = 4 }
public enum TerminalContentKind { Dashboard = 1, Mimic = 2 }

public sealed record TerminalEnrollmentPolicy
{
    public TerminalEnrollmentPolicy(TimeSpan challengeLifetime, TimeSpan credentialLifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(challengeLifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(credentialLifetime, TimeSpan.Zero);
        ChallengeLifetime = challengeLifetime;
        CredentialLifetime = credentialLifetime;
    }
    public TimeSpan ChallengeLifetime { get; }
    public TimeSpan CredentialLifetime { get; }
}

public sealed record TerminalContentAssignment(TerminalContentKind Kind, Guid ContentId);

public sealed record TerminalProfileSnapshot(
    TerminalProfileId ProfileId, string Name, TerminalContentAssignment? Content,
    StateVersion Version, DateTimeOffset UpdatedAt);

public sealed record TerminalSnapshot(
    TerminalId TerminalId, string Label, TerminalState State, TerminalProfileId? ProfileId,
    StateVersion Version, DateTimeOffset? LastSeenAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record TerminalEnrollmentChallenge(
    TerminalEnrollmentId EnrollmentId, TerminalId TerminalId, string Challenge,
    DateTimeOffset ExpiresAt);

public sealed record TerminalCredentialIssue(
    TerminalDeviceIdentityId DeviceIdentityId, TerminalId TerminalId, string Credential,
    DateTimeOffset ExpiresAt);

public sealed record TerminalCredentialPresentation(string Scheme, string Credential)
{
    public const string RequiredScheme = "Dispatcher-Terminal";
}

public sealed record AuthenticatedTerminal(
    TerminalDeviceIdentityId DeviceIdentityId, TerminalSnapshot Terminal,
    TerminalProfileSnapshot? Profile, TerminalContentAssignment? Content);

public sealed record InitiateTerminalEnrollment(
    TerminalEnrollmentId EnrollmentId, TerminalId TerminalId, string Label);

public sealed record CreateTerminalProfile(TerminalProfileId ProfileId, string Name);
public sealed record AssignTerminalProfile(TerminalId TerminalId, TerminalProfileId ProfileId, StateVersion ExpectedVersion);
public sealed record AssignTerminalContent(TerminalProfileId ProfileId, TerminalContentAssignment Content, StateVersion ExpectedVersion);
public sealed record ChangeTerminalState(TerminalId TerminalId, StateVersion ExpectedVersion);

public static class TerminalPermissions
{
    public static readonly PermissionCode Read = PermissionCode.From("terminals.fleet.read");
    public static readonly PermissionCode Manage = PermissionCode.From("terminals.fleet.manage");
    public static readonly PermissionCode ApproveEnrollment = PermissionCode.From("terminals.enrollment.approve");
}

public static class TerminalRequestIdentity
{
    public static TerminalCredentialPresentation? Resolve(
        string? authorizationHeader, IReadOnlyDictionary<string, string?> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return null;
        var separator = authorizationHeader.IndexOf(' ');
        return separator > 0 && separator < authorizationHeader.Length - 1
            ? new TerminalCredentialPresentation(
                authorizationHeader[..separator], authorizationHeader[(separator + 1)..].Trim())
            : null;
    }
}
