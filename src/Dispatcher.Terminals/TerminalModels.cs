using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;

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
public enum TerminalExperience { Kiosk = 1, Wallboard = 2 }
public enum TerminalOfflineMode { Blank = 1, ReadOnlyLastSynchronized = 2 }
public enum TerminalEmployeeReauthentication { NotRequired = 1, Required = 2 }

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

public sealed record TerminalRuntimePolicy(
    TerminalExperience Experience,
    TerminalOfflineMode OfflineMode,
    TerminalEmployeeReauthentication EmployeeReauthentication,
    IReadOnlyList<PermissionCode> RuntimePermissions)
{
    public static TerminalRuntimePolicy Default { get; } = new(
        TerminalExperience.Kiosk, TerminalOfflineMode.Blank,
        TerminalEmployeeReauthentication.NotRequired, []);
}

public sealed record TerminalPinPolicy
{
    public TerminalPinPolicy(int iterations, int minimumLength, int maximumLength, TimeSpan reauthenticationLifetime)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumLength, 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, minimumLength);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(reauthenticationLifetime, TimeSpan.Zero);
        Iterations = iterations;
        MinimumLength = minimumLength;
        MaximumLength = maximumLength;
        ReauthenticationLifetime = reauthenticationLifetime;
    }
    public int Iterations { get; }
    public int MinimumLength { get; }
    public int MaximumLength { get; }
    public TimeSpan ReauthenticationLifetime { get; }
}

public sealed record TerminalProfileSnapshot(
    TerminalProfileId ProfileId, string Name, TerminalContentAssignment? Content,
    TerminalRuntimePolicy RuntimePolicy, StateVersion Version, DateTimeOffset UpdatedAt);

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
public sealed record ConfigureTerminalRuntime(
    TerminalProfileId ProfileId, TerminalRuntimePolicy Policy, StateVersion ExpectedVersion);
public sealed record SetTerminalEmployeePin(TerminalProfileId ProfileId, PersonId PersonId, string Pin);
public sealed record ChangeTerminalState(TerminalId TerminalId, StateVersion ExpectedVersion);

public sealed record TerminalRuntimeSync(
    TerminalId TerminalId, TerminalDeviceIdentityId DeviceIdentityId,
    TerminalProfileId ProfileId, StateVersion ProfileVersion,
    TerminalContentAssignment Content, TerminalRuntimePolicy Policy,
    DateTimeOffset SynchronizedAt);

public sealed record TerminalHeartbeat(
    DateTimeOffset AcceptedAt, StateVersion ProfileVersion, bool ResyncRequired);

public sealed record TerminalEmployeeReauthenticationIssue(
    PersonId PersonId, string Token, DateTimeOffset ExpiresAt);

public sealed record TerminalEmployeeReauthenticationPresentation(string Token);

public sealed record TerminalInteractionAttribution(
    Guid InteractionId, TerminalId TerminalId, TerminalDeviceIdentityId DeviceIdentityId,
    PersonId? PersonId, string Action, DateTimeOffset AcceptedAt);

public static class TerminalCommandAdmission
{
    public static Result Evaluate(TerminalRuntimePolicy policy, bool online)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var code = !online
            ? "terminal.offline_command_not_queued"
            : policy.Experience == TerminalExperience.Wallboard
                ? "terminal.wallboard_command_denied"
                : "terminal.command_unavailable";
        return Result.Failure(new OperationError(ErrorCode.From(code),
            "Terminal command capability is unavailable and the request was not queued."));
    }
}

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
