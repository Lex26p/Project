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
public sealed class CommandExecutionTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 22, 17, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public CommandExecutionTests(PostgreSqlClusterFixture cluster) => this.cluster = cluster;

    [Fact]
    public async Task SimulatorExecutionKeepsUnknownReconcilableAndFencesIdentityAndPhysicalEffects()
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
        const string password = "Command-Execution-38!";
        var subjectId = SubjectId.New();
        Assert.True((await identity.BootstrapAdministratorAsync(new(
            IdentityAccountId.New(), subjectId, null, IdentityRoleId.New(), "execution-operator", password))).IsSuccess);
        var login = await identity.LoginAsync(new("execution-operator", password));
        Assert.True(login.IsSuccess);

        var scopeId = RuntimeScopeId.From(Guid.NewGuid());
        var pointId = PointId.From(Guid.NewGuid());
        var session = Elevated(login.Value.Session, scopeId, pointId);
        var acquire = SessionAuthorization.AuthorizeMutation(session, CommandPermissions.AcquireLease, clock).Value;
        var prepare = SessionAuthorization.AuthorizeMutation(session, CommandPermissions.Prepare, clock).Value;
        var execute = SessionAuthorization.AuthorizeMutation(session, CommandPermissions.Execute, clock).Value;
        var security = new CommandStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock,
            new CommandSecurityPolicy(TimeSpan.FromMinutes(5), false, TimeSpan.FromSeconds(30)));
        var leaseId = ControlLeaseId.New();
        Assert.True((await security.AcquireLeaseAsync(
            acquire, new(leaseId, scopeId, TimeSpan.FromMinutes(3)), null)).IsSuccess);
        var active = Active(scopeId, pointId);
        var current = Current(scopeId, pointId, 10);

        var staleIntent = await PrepareAsync(security, prepare, leaseId, scopeId, pointId, active, current, 12);
        var direct = new CommandExecutionStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        Assert.Equal("command.active_revision_stale", (await direct.ExecuteAsync(execute, new(
            CommandExecutionId.New(), staleIntent.IntentId, scopeId, pointId),
            active with { Generation = active.Generation + 1 }, current)).Error?.Code.Value);
        Assert.Equal("command.current_stale", (await direct.ExecuteAsync(execute, new(
            CommandExecutionId.New(), staleIntent.IntentId, scopeId, pointId),
            active, Current(scopeId, pointId, 11))).Error?.Code.Value);
        await security.RecordSafetyBlockAsync(new(Guid.NewGuid(), scopeId, true, "blocked", Start));
        Assert.Equal("command.safety_stale", (await direct.ExecuteAsync(execute, new(
            CommandExecutionId.New(), staleIntent.IntentId, scopeId, pointId), active, current)).Error?.Code.Value);
        await security.RecordSafetyBlockAsync(new(Guid.NewGuid(), scopeId, false, "cleared", Start.AddSeconds(1)));

        var intent = await PrepareAsync(security, prepare, leaseId, scopeId, pointId, active, current, 12);
        var timeout = new CommandExecutionStore(
            dataSource, PostgreSqlClusterFixture.OwnerBRole, clock,
            new SimulatorCommandCommitHook(_ => throw new TimeoutException("response lost after Simulator commit")));
        var executionId = CommandExecutionId.New();
        var unknown = await timeout.ExecuteAsync(execute, new(
            executionId, intent.IntentId, scopeId, pointId), active, current);
        Assert.True(unknown.IsSuccess);
        Assert.Equal(CommandExecutionState.Unknown, unknown.Value.State);
        Assert.Null(unknown.Value.CompletedAt);

        var restarted = new CommandExecutionStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        var reconciled = await restarted.ReconcileAsync(execute, executionId, scopeId, pointId);
        Assert.True(reconciled.IsSuccess);
        Assert.Equal(CommandExecutionState.Succeeded, reconciled.Value.State);
        Assert.Equal(12, reconciled.Value.ResultValue);
        Assert.Equal(CommandExecutionDisposition.Reconciled, reconciled.Value.Disposition);
        Assert.Equal(CommandExecutionDisposition.Replayed, (await restarted.ExecuteAsync(execute, new(
            executionId, intent.IntentId, scopeId, pointId), active, current)).Value.Disposition);
        Assert.Equal("command.intent_already_executed", (await restarted.ExecuteAsync(execute, new(
            CommandExecutionId.New(), intent.IntentId, scopeId, pointId), active, current)).Error?.Code.Value);

        var newSession = Elevated(new SessionSnapshot(
            SessionId.New(), session.SubjectId, session.PrincipalKind, session.IssuedAt, session.ExpiresAt,
            new EffectivePermissions([])), scopeId, pointId);
        var newSessionExecute = SessionAuthorization.AuthorizeMutation(
            newSession, CommandPermissions.Execute, clock).Value;
        Assert.Equal("command.execution_identity_session", (await restarted.ReconcileAsync(
            newSessionExecute, executionId, scopeId, pointId)).Error?.Code.Value);
        Assert.Equal("command.execution_identity_session", (await restarted.ExecuteAsync(newSessionExecute, new(
            executionId, intent.IntentId, scopeId, pointId), active, current)).Error?.Code.Value);

        var rejectedIntent = await PrepareAsync(security, prepare, leaseId, scopeId, pointId, active, current, 100);
        var rejected = await restarted.ExecuteAsync(execute, new(
            CommandExecutionId.New(), rejectedIntent.IntentId, scopeId, pointId), active, current);
        Assert.Equal(CommandExecutionState.Rejected, rejected.Value.State);
        Assert.Equal("simulator.value_out_of_range", rejected.Value.RejectionCode);

        var duplicateIntent = await PrepareAsync(security, prepare, leaseId, scopeId, pointId, active, current, 13);
        var duplicateId = CommandExecutionId.New();
        var duplicateRequest = new ExecuteSimulatorCommand(duplicateId, duplicateIntent.IntentId, scopeId, pointId);
        var duplicateResults = await Task.WhenAll(
            restarted.ExecuteAsync(execute, duplicateRequest, active, current),
            restarted.ExecuteAsync(execute, duplicateRequest, active, current));
        Assert.All(duplicateResults, result => Assert.True(result.IsSuccess));
        Assert.Equal(CommandExecutionState.Succeeded,
            (await restarted.ReconcileAsync(execute, duplicateId, scopeId, pointId)).Value.State);

        var snapshot = await restarted.ReadSnapshotAsync(session, scopeId);
        Assert.True(snapshot.IsSuccess);
        Assert.Contains(snapshot.Value.Executions, value => value.ExecutionId == executionId && value.State == CommandExecutionState.Succeeded);
        var feed = await restarted.ReadFeedAsync(session, scopeId, 0);
        Assert.True(feed.IsSuccess);
        Assert.Contains(feed.Value.Transitions, value => value.ExecutionId == executionId && value.State == CommandExecutionState.Accepted);
        Assert.Contains(feed.Value.Transitions, value => value.ExecutionId == executionId && value.State == CommandExecutionState.InProgress);
        Assert.Contains(feed.Value.Transitions, value => value.ExecutionId == executionId && value.State == CommandExecutionState.Unknown);
        Assert.Contains(feed.Value.Transitions, value => value.ExecutionId == executionId && value.State == CommandExecutionState.Succeeded);
        var withoutPoint = new SessionSnapshot(
            session.Id, session.SubjectId, session.PrincipalKind, session.IssuedAt, session.ExpiresAt,
            new EffectivePermissions([CommandPermissions.Execute, CommandPermissions.ControlScope(scopeId)]));
        Assert.Empty((await restarted.ReadSnapshotAsync(withoutPoint, scopeId)).Value.Executions);

        var expiringIntent = await PrepareAsync(security, prepare, leaseId, scopeId, pointId, active, current, 11);
        Assert.True((await security.RevokeLeaseAsync(acquire, leaseId, "operator-release")).IsSuccess);
        Assert.Equal("command.lease_inactive", (await restarted.ExecuteAsync(execute, new(
            CommandExecutionId.New(), expiringIntent.IntentId, scopeId, pointId), active, current)).Error?.Code.Value);
        Assert.Equal("session.revoked", SessionAuthorization.AuthorizeMutation(
            session.Revoke(clock.GetUtcNow()), CommandPermissions.Execute, clock).Error?.Code.Value);

        await using var connection = await dataSource.OpenConnectionAsync();
        await using (var count = new NpgsqlCommand(
            "SELECT count(*) FROM command.simulator_execution_receipt WHERE execution_id=@id;", connection))
        {
            count.Parameters.AddWithValue("id", duplicateId.Value);
            Assert.Equal(1L, await count.ExecuteScalarAsync());
        }
        await using (var audit = new NpgsqlCommand(
            "SELECT count(*) FROM command.command_audit WHERE execution_id=@id;", connection))
        {
            audit.Parameters.AddWithValue("id", executionId.Value);
            Assert.True((long)(await audit.ExecuteScalarAsync())! >= 4);
        }
        await using (var mutation = new NpgsqlCommand(
            "UPDATE command.simulator_execution_receipt SET result_value=99 WHERE execution_id=@id;", connection))
        {
            mutation.Parameters.AddWithValue("id", executionId.Value);
            await Assert.ThrowsAsync<PostgresException>(() => mutation.ExecuteNonQueryAsync());
        }
        Assert.DoesNotContain(typeof(CommandExecutionStore).Assembly.GetReferencedAssemblies(), reference =>
            reference.Name is "Dispatcher.Modbus" or "Dispatcher.Snmp" or "Dispatcher.Protocols");
    }

    private static async Task<PreparedCommandIntent> PrepareAsync(
        CommandStore store, AuthorizedMutation authorization, ControlLeaseId leaseId,
        RuntimeScopeId scopeId, PointId pointId, ActiveSimulatorManifest active,
        CurrentSnapshot current, long desired)
    {
        var result = await store.PrepareAsync(authorization, new(
            CommandIntentId.New(), leaseId, scopeId, pointId, desired, Unit.FromSymbol("kW"),
            CommandInteractionMode.Live, active.Receipt.RevisionId, active.Receipt.RevisionNumber,
            active.Generation, active.Receipt.ManifestFingerprint, current.Position.Value), active, current);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static SessionSnapshot Elevated(SessionSnapshot source, RuntimeScopeId scopeId, PointId pointId) => new(
        source.Id, source.SubjectId, source.PrincipalKind, source.IssuedAt, source.ExpiresAt,
        new EffectivePermissions([
            CommandPermissions.AcquireLease, CommandPermissions.Prepare, CommandPermissions.Execute,
            CommandPermissions.ControlScope(scopeId), CommandPermissions.ControlPoint(pointId),
        ]));

    private static ActiveSimulatorManifest Active(RuntimeScopeId scopeId, PointId pointId)
    {
        var receipt = new SimulatorManifestReceipt(
            ConfigurationRevisionId.From(Guid.NewGuid()), FacilityScopeId.From(scopeId.Value), RevisionNumber.From(1), null,
            new string('c', 64), new string('d', 64), SimulatorManifestStatus.Activated,
            null, null, Start, Start, Start);
        return new ActiveSimulatorManifest(receipt, new SimulatorScenarioConfig(
            scopeId, SourceId.From(Guid.NewGuid()), 38,
            [new SimulatorPointConfig(pointId, 10, 5, Unit.FromSymbol("kW"))]), 1);
    }

    private static CurrentSnapshot Current(RuntimeScopeId scopeId, PointId pointId, ulong position)
    {
        var entry = new CurrentEntry(
            scopeId, SourceId.From(Guid.NewGuid()), pointId,
            SourceBindingGeneration.From(1), SourceSessionGeneration.From(1),
            new OwnerPosition<SourceObservation>(1), new OwnerPosition<CurrentEntry>(position),
            TypedValue.From(10L), Unit.FromSymbol("kW"), DataQuality.Good, Freshness.Fresh,
            SourceTimestamp.FromUtc(Start), ReceiveTimestamp.FromUtc(Start),
            ProcessedTimestamp.FromUtc(Start), new MonotonicTimestamp(1));
        return new CurrentSnapshot(scopeId, new OwnerPosition<CurrentEntry>(position), [entry]);
    }

    private sealed class MutableClock(DateTimeOffset now) : IWallClock
    {
        private DateTimeOffset current = now;
        public DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan value) => current = current.Add(value);
    }
}
