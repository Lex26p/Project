using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Dispatcher.Semantics;

namespace Dispatcher.Platform;

public enum PrincipalKind
{
    User = 1,
    Device = 2,
    Workload = 3,
}

public sealed partial record PermissionCode
{
    private PermissionCode(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static PermissionCode From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        return PermissionPattern().IsMatch(normalized)
            ? new PermissionCode(normalized)
            : throw new ArgumentException(
                "A permission must use lowercase dot-separated names without wildcards.",
                nameof(value));
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z][a-z0-9_-]*(\\.[a-z][a-z0-9_-]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PermissionPattern();
}

public sealed class EffectivePermissions
{
    private readonly HashSet<PermissionCode> grants;
    private readonly HashSet<PermissionCode> denials;

    public EffectivePermissions(IEnumerable<PermissionCode> grants, IEnumerable<PermissionCode>? denials = null)
    {
        ArgumentNullException.ThrowIfNull(grants);
        this.grants = grants.ToHashSet();
        this.denials = denials?.ToHashSet() ?? [];
    }

    public IReadOnlySet<PermissionCode> Grants => new ReadOnlySet<PermissionCode>(grants);

    public IReadOnlySet<PermissionCode> Denials => new ReadOnlySet<PermissionCode>(denials);

    public bool Allows(PermissionCode permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return grants.Contains(permission) && !denials.Contains(permission);
    }
}

public readonly record struct SubjectId
{
    private readonly CanonicalId<SubjectId> value;

    private SubjectId(CanonicalId<SubjectId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static SubjectId New() => new(CanonicalId.New<SubjectId>());

    public static SubjectId From(Guid value) => new(CanonicalId.From<SubjectId>(value));
}

public readonly record struct SessionId
{
    private readonly CanonicalId<SessionId> value;

    private SessionId(CanonicalId<SessionId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static SessionId New() => new(CanonicalId.New<SessionId>());

    public static SessionId From(Guid value) => new(CanonicalId.From<SessionId>(value));
}

public sealed record SessionSnapshot
{
    public SessionSnapshot(
        SessionId id,
        SubjectId subjectId,
        PrincipalKind principalKind,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        EffectivePermissions permissions,
        DateTimeOffset? revokedAt = null)
    {
        if (!Enum.IsDefined(principalKind))
        {
            throw new ArgumentOutOfRangeException(nameof(principalKind));
        }

        RequireUtc(issuedAt, nameof(issuedAt));
        RequireUtc(expiresAt, nameof(expiresAt));
        if (expiresAt <= issuedAt)
        {
            throw new ArgumentException("Session expiry must be after issuance.", nameof(expiresAt));
        }

        if (revokedAt is not null)
        {
            RequireUtc(revokedAt.Value, nameof(revokedAt));
        }

        ArgumentNullException.ThrowIfNull(permissions);
        _ = id.Value;
        _ = subjectId.Value;
        Id = id;
        SubjectId = subjectId;
        PrincipalKind = principalKind;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        Permissions = permissions;
        RevokedAt = revokedAt;
    }

    public SessionId Id { get; }

    public SubjectId SubjectId { get; }

    public PrincipalKind PrincipalKind { get; }

    public DateTimeOffset IssuedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public EffectivePermissions Permissions { get; }

    public DateTimeOffset? RevokedAt { get; }

    public SessionSnapshot Revoke(DateTimeOffset revokedAt) => new(
        Id,
        SubjectId,
        PrincipalKind,
        IssuedAt,
        ExpiresAt,
        Permissions,
        revokedAt);

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Session timestamps must use the UTC offset.", parameterName);
        }
    }
}

public sealed class AuthorizedMutation
{
    internal AuthorizedMutation(SessionSnapshot session, PermissionCode permission)
    {
        Session = session;
        Permission = permission;
    }

    public SessionSnapshot Session { get; }

    public PermissionCode Permission { get; }
}

public sealed class AuthorizedAccess
{
    internal AuthorizedAccess(SessionSnapshot session, PermissionCode permission)
    {
        Session = session;
        Permission = permission;
    }

    public SessionSnapshot Session { get; }

    public PermissionCode Permission { get; }
}

public static class SessionAuthorization
{
    public static Result<SessionSnapshot> ValidateSession(SessionSnapshot? session, IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (session is null)
        {
            return Result.Failure<SessionSnapshot>(Error("session.anonymous", "A session is required."));
        }

        var now = clock.GetUtcNow();
        if (session.RevokedAt is not null)
        {
            return Result.Failure<SessionSnapshot>(Error("session.revoked", "The session is revoked."));
        }

        if (session.ExpiresAt <= now)
        {
            return Result.Failure<SessionSnapshot>(Error("session.expired", "The session has expired."));
        }

        return Result.Success(session);
    }

    public static Result<AuthorizedAccess> AuthorizeAccess(
        SessionSnapshot? session,
        PermissionCode requiredPermission,
        IWallClock clock)
    {
        var validation = Validate(session, requiredPermission, clock);
        return validation.IsFailure
            ? Result.Failure<AuthorizedAccess>(validation.Error!)
            : Result.Success(new AuthorizedAccess(session!, requiredPermission));
    }

    public static Result<AuthorizedMutation> AuthorizeMutation(
        SessionSnapshot? session,
        PermissionCode requiredPermission,
        IWallClock clock)
    {
        var validation = Validate(session, requiredPermission, clock);
        return validation.IsFailure
            ? Result.Failure<AuthorizedMutation>(validation.Error!)
            : Result.Success(new AuthorizedMutation(session!, requiredPermission));
    }

    private static Result Validate(
        SessionSnapshot? session,
        PermissionCode requiredPermission,
        IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(requiredPermission);
        var sessionValidation = ValidateSession(session, clock);
        if (sessionValidation.IsFailure)
        {
            return Result.Failure(sessionValidation.Error!);
        }

        if (!sessionValidation.Value.Permissions.Allows(requiredPermission))
        {
            return Result.Failure(Error("permission.denied", "The required permission is not effective."));
        }

        return Result.Success();
    }

    private static OperationError Error(string code, string message) =>
        new(ErrorCode.From(code), message);
}

public sealed record TestIdentityOptions(
    bool Enabled,
    string EnvironmentName,
    PrincipalKind PrincipalKind,
    IReadOnlyCollection<PermissionCode> Permissions,
    TimeSpan Lifetime);

public static class TestIdentityIssuer
{
    public static Result<SessionSnapshot> Issue(TestIdentityOptions options, IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        var allowedEnvironment = string.Equals(options.EnvironmentName, "Development", StringComparison.Ordinal) ||
                                 string.Equals(options.EnvironmentName, "Test", StringComparison.Ordinal);
        if (!options.Enabled || !allowedEnvironment)
        {
            return Result.Failure<SessionSnapshot>(new OperationError(
                ErrorCode.From("test_identity.disabled"),
                "Test identity is disabled for this environment."));
        }

        if (options.Lifetime <= TimeSpan.Zero || options.Lifetime > TimeSpan.FromHours(8))
        {
            return Result.Failure<SessionSnapshot>(new OperationError(
                ErrorCode.From("test_identity.invalid_lifetime"),
                "Test identity lifetime must be between zero and eight hours."));
        }

        var now = clock.GetUtcNow();
        return Result.Success(new SessionSnapshot(
            SessionId.New(),
            SubjectId.New(),
            options.PrincipalKind,
            now,
            now.Add(options.Lifetime),
            new EffectivePermissions(options.Permissions)));
    }
}
