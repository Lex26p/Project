using Dispatcher.Command;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Facilities;
using Dispatcher.Identity;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Simulator;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class CommandSecurityTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 22, 15, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public CommandSecurityTests(PostgreSqlClusterFixture cluster) => this.cluster = cluster;

    [Fact]
    public async Task SimulatorPreflightRequiresBoundLeaseFreshEvidenceAndHasNoExecutor()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, IdentityMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, CommandMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        var clock = new MutableClock(Start);
        var identity = new IdentityStore(dataSource, PostgreSqlClusterFixture.OwnerARole, clock,
            new IdentitySecurityPolicy(10_000, 12, 128, 3, TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)));
        const string password = "Command-Step-Up-37!";
        var subjectId = SubjectId.New();
        Assert.True((await identity.BootstrapAdministratorAsync(new(
            IdentityAccountId.New(), subjectId, null, IdentityRoleId.New(), "command-operator", password))).IsSuccess);
        var login = await identity.LoginAsync(new("command-operator", password));
        Assert.True(login.IsSuccess);

        var scopeId = RuntimeScopeId.From(Guid.NewGuid());
        var pointId = PointId.From(Guid.NewGuid());
        var session = Elevated(login.Value.Session, scopeId, pointId);
        var acquire = SessionAuthorization.AuthorizeMutation(session, CommandPermissions.AcquireLease, clock).Value;
        var prepare = SessionAuthorization.AuthorizeMutation(session, CommandPermissions.Prepare, clock).Value;
        var policy = new CommandSecurityPolicy(TimeSpan.FromMinutes(5), stepUpRequired: true, TimeSpan.FromSeconds(30));
        var store = new CommandStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock, policy);

        Assert.Equal("identity.step_up_invalid", (await identity.VerifyStepUpAsync(
            session, "Wrong-Step-Up-37!", policy.StepUpLifetime)).Error?.Code.Value);
        var stepUp = await identity.VerifyStepUpAsync(session, password, policy.StepUpLifetime);
        var leaseId = ControlLeaseId.New();
        Assert.Equal("command.step_up_required", (await store.AcquireLeaseAsync(
            acquire, new(leaseId, scopeId, TimeSpan.FromMinutes(2)), null)).Error?.Code.Value);
        var lease = await store.AcquireLeaseAsync(
            acquire, new(leaseId, scopeId, TimeSpan.FromMinutes(2)), stepUp.Value);
        Assert.True(lease.IsSuccess);
        Assert.Equal(ControlLeaseState.Active, lease.Value.State(clock.GetUtcNow()));
        Assert.Equal("command.lease_held", (await store.AcquireLeaseAsync(
            acquire, new(ControlLeaseId.New(), scopeId, TimeSpan.FromMinutes(1)), stepUp.Value)).Error?.Code.Value);

        var active = Active(scopeId, pointId);
        var current = Current(scopeId, pointId, DataQuality.Good, Freshness.Fresh, 10);
        var request = Request(leaseId, scopeId, pointId, active, current, CommandInteractionMode.Live);
        var withoutPoint = new SessionSnapshot(
            session.Id, session.SubjectId, session.PrincipalKind, session.IssuedAt, session.ExpiresAt,
            new EffectivePermissions([CommandPermissions.Prepare, CommandPermissions.ControlScope(scopeId)]));
        var withoutPointAuthorization = SessionAuthorization.AuthorizeMutation(
            withoutPoint, CommandPermissions.Prepare, clock).Value;
        Assert.Equal("permission.denied", (await store.PrepareAsync(
            withoutPointAuthorization, request, active, current)).Error?.Code.Value);

        var otherSession = Elevated(new SessionSnapshot(
            SessionId.New(), SubjectId.New(), PrincipalKind.User, Start.AddMinutes(-1), Start.AddHours(1),
            new EffectivePermissions([])), scopeId, pointId);
        var otherPrepare = SessionAuthorization.AuthorizeMutation(otherSession, CommandPermissions.Prepare, clock).Value;
        Assert.Equal("command.lease_holder_mismatch", (await store.PrepareAsync(
            otherPrepare, request, active, current)).Error?.Code.Value);
        Assert.Equal("command.history_mode_denied", (await store.PrepareAsync(
            prepare, request with { InteractionMode = CommandInteractionMode.History }, active, current)).Error?.Code.Value);
        Assert.Equal("command.active_revision_stale", (await store.PrepareAsync(
            prepare, request with { ExpectedGeneration = active.Generation + 1 }, active, current)).Error?.Code.Value);
        Assert.Equal("command.current_stale", (await store.PrepareAsync(
            prepare, request with { ExpectedCurrentPosition = current.Position.Value + 1 }, active, current)).Error?.Code.Value);
        Assert.Equal("command.quality_blocked", (await store.PrepareAsync(
            prepare, request, active, Current(scopeId, pointId, DataQuality.Bad, Freshness.Stale, 10))).Error?.Code.Value);

        var blocked = await store.RecordSafetyBlockAsync(new(
            Guid.NewGuid(), scopeId, true, "maintenance_block", Start));
        Assert.Equal(StateVersion.From(2), blocked.Value);
        Assert.Equal("command.safety_blocked", (await store.PrepareAsync(
            prepare, request, active, current)).Error?.Code.Value);
        var unblockedFact = new CommandSafetyBlockFact(Guid.NewGuid(), scopeId, false, "cleared", Start.AddSeconds(1));
        var unblocked = await store.RecordSafetyBlockAsync(unblockedFact);
        Assert.Equal(StateVersion.From(3), unblocked.Value);
        Assert.Equal(unblocked.Value, (await store.RecordSafetyBlockAsync(unblockedFact)).Value);

        var prepared = await store.PrepareAsync(prepare, request, active, current);
        Assert.True(prepared.IsSuccess);
        Assert.Equal(active.Receipt.RevisionId, prepared.Value.RevisionId);
        Assert.Equal(current.Position.Value, prepared.Value.CurrentPosition);
        Assert.Equal(unblocked.Value, prepared.Value.SafetyVersion);
        Assert.Equal(prepared.Value, (await store.PrepareAsync(prepare, request, active, current)).Value);
        Assert.Equal("command.intent_conflict", (await store.PrepareAsync(
            prepare, request with { DesiredValue = request.DesiredValue + 1 }, active, current)).Error?.Code.Value);

        Assert.True((await store.RecordSafetyBlockAsync(new(
            Guid.NewGuid(), scopeId, true, "post_prepare_block", Start.AddSeconds(2)))).IsSuccess);
        var revoked = await store.RevokeLeaseAsync(acquire, leaseId, "operator-cancelled");
        Assert.Equal(ControlLeaseState.Revoked, revoked.Value.State(clock.GetUtcNow()));
        Assert.Equal("command.lease_inactive", (await store.PrepareAsync(
            prepare, request with { IntentId = CommandIntentId.New() }, active, current)).Error?.Code.Value);
        Assert.Equal("command.step_up_replayed", (await store.AcquireLeaseAsync(
            acquire, new(ControlLeaseId.New(), scopeId, TimeSpan.FromMinutes(1)), stepUp.Value)).Error?.Code.Value);

        await store.RecordSafetyBlockAsync(new(Guid.NewGuid(), scopeId, false, "cleared", Start.AddSeconds(3)));
        var freshStepUp = await identity.VerifyStepUpAsync(session, password, policy.StepUpLifetime);
        var expiringLeaseId = ControlLeaseId.New();
        Assert.True((await store.AcquireLeaseAsync(acquire,
            new(expiringLeaseId, scopeId, TimeSpan.FromSeconds(5)), freshStepUp.Value)).IsSuccess);
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal("command.lease_inactive", (await store.PrepareAsync(
            prepare, Request(expiringLeaseId, scopeId, pointId, active, current, CommandInteractionMode.Live),
            active, current)).Error?.Code.Value);
        Assert.Equal("session.revoked", SessionAuthorization.AuthorizeMutation(
            session.Revoke(clock.GetUtcNow()), CommandPermissions.Prepare, clock).Error?.Code.Value);

        Assert.DoesNotContain(typeof(CommandStore).GetMethods(), method => method.Name.Contains("Execute", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(CommandStore).Assembly.GetReferencedAssemblies(), reference =>
            reference.Name is "Dispatcher.Modbus" or "Dispatcher.Snmp" or "Dispatcher.Protocols");
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var mutation = new NpgsqlCommand(
            "UPDATE command.prepared_intent SET desired_value=99 WHERE intent_id=@id;", connection);
        mutation.Parameters.AddWithValue("id", request.IntentId.Value);
        await Assert.ThrowsAsync<PostgresException>(() => mutation.ExecuteNonQueryAsync());
    }

    private static SessionSnapshot Elevated(SessionSnapshot source, RuntimeScopeId scopeId, PointId pointId) => new(
        source.Id, source.SubjectId, source.PrincipalKind, source.IssuedAt, source.ExpiresAt,
        new EffectivePermissions([
            CommandPermissions.AcquireLease, CommandPermissions.Prepare,
            CommandPermissions.ControlScope(scopeId), CommandPermissions.ControlPoint(pointId),
        ]));

    private static ActiveSimulatorManifest Active(RuntimeScopeId scopeId, PointId pointId)
    {
        var revisionId = ConfigurationRevisionId.From(Guid.NewGuid());
        var fingerprint = new string('a', 64);
        var receipt = new SimulatorManifestReceipt(
            revisionId, FacilityScopeId.From(scopeId.Value), RevisionNumber.From(1), null,
            fingerprint, new string('b', 64), SimulatorManifestStatus.Activated,
            null, null, Start, Start, Start);
        return new ActiveSimulatorManifest(receipt, new SimulatorScenarioConfig(
            scopeId, SourceId.From(Guid.NewGuid()), 37,
            [new SimulatorPointConfig(pointId, 10, 5, Unit.FromSymbol("kW"))]), 1);
    }

    private static CurrentSnapshot Current(
        RuntimeScopeId scopeId, PointId pointId, DataQuality quality, Freshness freshness, ulong position)
    {
        var entry = new CurrentEntry(
            scopeId, SourceId.From(Guid.NewGuid()), pointId,
            SourceBindingGeneration.From(1), SourceSessionGeneration.From(1),
            new OwnerPosition<SourceObservation>(1), new OwnerPosition<CurrentEntry>(position),
            TypedValue.From(10L), Unit.FromSymbol("kW"), quality, freshness,
            SourceTimestamp.FromUtc(Start), ReceiveTimestamp.FromUtc(Start),
            ProcessedTimestamp.FromUtc(Start), new MonotonicTimestamp(1));
        return new CurrentSnapshot(scopeId, new OwnerPosition<CurrentEntry>(position), [entry]);
    }

    private static PrepareSimulatorCommand Request(
        ControlLeaseId leaseId, RuntimeScopeId scopeId, PointId pointId,
        ActiveSimulatorManifest active, CurrentSnapshot current, CommandInteractionMode mode) => new(
            CommandIntentId.New(), leaseId, scopeId, pointId, 12, Unit.FromSymbol("kW"), mode,
            active.Receipt.RevisionId, active.Receipt.RevisionNumber, active.Generation,
            active.Receipt.ManifestFingerprint, current.Position.Value);

    private sealed class MutableClock(DateTimeOffset now) : IWallClock
    {
        private DateTimeOffset current = now;
        public DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan value) => current = current.Add(value);
    }
}
