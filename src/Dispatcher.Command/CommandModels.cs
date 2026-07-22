using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Identity;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Simulator;

namespace Dispatcher.Command;

public readonly record struct ControlLeaseId
{
    private readonly CanonicalId<ControlLeaseId> value;
    private ControlLeaseId(CanonicalId<ControlLeaseId> value) => this.value = value;
    public Guid Value => value.Value;
    public static ControlLeaseId New() => new(CanonicalId.New<ControlLeaseId>());
    public static ControlLeaseId From(Guid value) => new(CanonicalId.From<ControlLeaseId>(value));
}

public readonly record struct CommandIntentId
{
    private readonly CanonicalId<CommandIntentId> value;
    private CommandIntentId(CanonicalId<CommandIntentId> value) => this.value = value;
    public Guid Value => value.Value;
    public static CommandIntentId New() => new(CanonicalId.New<CommandIntentId>());
    public static CommandIntentId From(Guid value) => new(CanonicalId.From<CommandIntentId>(value));
}

public enum CommandInteractionMode { Live = 1, History = 2 }
public enum ControlLeaseState { Active = 1, Revoked = 2, Expired = 3 }

public sealed record CommandSecurityPolicy
{
    public CommandSecurityPolicy(TimeSpan maximumLeaseLifetime, bool stepUpRequired, TimeSpan stepUpLifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumLeaseLifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stepUpLifetime, TimeSpan.Zero);
        MaximumLeaseLifetime = maximumLeaseLifetime;
        StepUpRequired = stepUpRequired;
        StepUpLifetime = stepUpLifetime;
    }
    public TimeSpan MaximumLeaseLifetime { get; }
    public bool StepUpRequired { get; }
    public TimeSpan StepUpLifetime { get; }
}

public sealed record AcquireControlLease(
    ControlLeaseId LeaseId, RuntimeScopeId ScopeId, TimeSpan Lifetime);
public sealed record ControlLeaseSnapshot(
    ControlLeaseId LeaseId, RuntimeScopeId ScopeId, SessionId SessionId, SubjectId SubjectId,
    DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt,
    string? RevocationReason, StateVersion Version)
{
    public ControlLeaseState State(DateTimeOffset now) => RevokedAt is not null
        ? ControlLeaseState.Revoked
        : ExpiresAt <= now ? ControlLeaseState.Expired : ControlLeaseState.Active;
}

public sealed record CommandSafetyBlockFact(
    Guid FactId, RuntimeScopeId ScopeId, bool Blocked, string ReasonCode, DateTimeOffset ObservedAt);

public sealed record PrepareSimulatorCommand(
    CommandIntentId IntentId, ControlLeaseId LeaseId, RuntimeScopeId ScopeId, PointId PointId,
    long DesiredValue, Unit Unit, CommandInteractionMode InteractionMode,
    ConfigurationRevisionId ExpectedRevisionId, RevisionNumber ExpectedRevisionNumber,
    long ExpectedGeneration, string ExpectedManifestFingerprint, ulong ExpectedCurrentPosition);

public sealed record PreparedCommandIntent(
    CommandIntentId IntentId, ControlLeaseId LeaseId, RuntimeScopeId ScopeId, PointId PointId,
    long DesiredValue, Unit Unit, ConfigurationRevisionId RevisionId, RevisionNumber RevisionNumber,
    long ManifestGeneration, string ManifestFingerprint, ulong CurrentPosition, long CurrentValue,
    DataQuality Quality, Freshness Freshness, StateVersion SafetyVersion,
    DateTimeOffset PreparedAt, DateTimeOffset ExpiresAt);

public static class CommandPermissions
{
    public static readonly PermissionCode AcquireLease = PermissionCode.From("commands.control.acquire");
    public static readonly PermissionCode Prepare = PermissionCode.From("commands.intent.prepare");
    public static PermissionCode ControlScope(RuntimeScopeId scopeId) =>
        PermissionCode.From($"commands.scope.s{scopeId.Value:N}.control");
    public static PermissionCode ControlPoint(PointId pointId) =>
        PermissionCode.From($"commands.point.p{pointId.Value:N}.control");
}
