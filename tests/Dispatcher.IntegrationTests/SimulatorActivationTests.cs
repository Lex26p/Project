using System.Text.Json;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Facilities;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Server;
using Dispatcher.Simulator;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class SimulatorActivationTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 19, 10, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public SimulatorActivationTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task ReceiptValidationAndActivationSurviveDuplicatesReorderRejectionAndCrash()
    {
        await using var context = await SimulatorTestContext.CreateAsync(cluster);
        var revision1 = ConfigurationRevisionId.New();
        var revision2 = ConfigurationRevisionId.New();
        var revision3 = ConfigurationRevisionId.New();
        var manifest1 = CreateManifest(
            Guid.Parse("81000000-0000-0000-0000-000000000001"),
            17,
            [(Guid.Parse("82000000-0000-0000-0000-000000000001"), 10L)]);
        var manifest2 = CreateManifest(
            Guid.Parse("81000000-0000-0000-0000-000000000002"),
            23,
            [
                (Guid.Parse("82000000-0000-0000-0000-000000000002"), 20L),
                (Guid.Parse("82000000-0000-0000-0000-000000000003"), 30L),
            ]);
        var delivery1 = Delivery(revision1, 1, manifest1);
        var delivery2 = Delivery(revision2, 2, manifest2);

        Assert.Equal(
            "simulator.manifest_reordered",
            (await context.Store.ReceiveAsync(delivery2, "receiver-a")).Error?.Code.Value);

        var received = await context.Store.ReceiveAsync(delivery1, "receiver-a");
        Assert.Equal(SimulatorManifestStatus.Received, received.Value.Status);
        var duplicate = await context.Store.ReceiveAsync(delivery1, "receiver-b");
        Assert.Equal(received.Value, duplicate.Value);
        Assert.Equal(1L, await context.CountAuditAsync("manifest.receive"));

        var conflict = delivery1 with { DependencyFingerprint = new string('B', 64) };
        Assert.Equal(
            "simulator.manifest_conflict",
            (await context.Store.ReceiveAsync(conflict, "receiver-b")).Error?.Code.Value);
        await context.AssertManifestIsImmutableAsync(revision1);

        var validated1 = await context.Store.ValidateAsync(context.ScopeId, revision1, "validator-a");
        Assert.Equal(SimulatorManifestStatus.Validated, validated1.Value.Status);
        var active1 = await context.Store.ActivateAsync(context.ScopeId, revision1, "activator-a");
        Assert.Equal(1, active1.Value.Generation);
        Assert.Equal([10L], active1.Value.Configuration.Points.Select(point => point.Baseline));

        Assert.True((await context.Store.ReceiveAsync(delivery2, "receiver-a")).IsSuccess);
        Assert.Equal(
            SimulatorManifestStatus.Validated,
            (await context.Store.ValidateAsync(context.ScopeId, revision2, "validator-a")).Value.Status);
        var crashStore = context.CreateStore(
            new SimulatorActivationCommitHook(_ => throw new InvalidOperationException("simulated crash")));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => crashStore.ActivateAsync(context.ScopeId, revision2, "activator-crash"));

        var restarted = context.CreateStore();
        var restored1 = await restarted.ReadActiveAsync(context.ScopeId);
        Assert.Equal(revision1, restored1.Value.Receipt.RevisionId);
        Assert.Equal(1, restored1.Value.Generation);

        var active2 = await restarted.ActivateAsync(context.ScopeId, revision2, "activator-b");
        Assert.Equal(2, active2.Value.Generation);
        Assert.Equal(revision2, active2.Value.Receipt.RevisionId);
        Assert.Equal(
            [20L, 30L],
            active2.Value.Configuration.Points.Select(point => point.Baseline).Order());
        Assert.DoesNotContain(
            active2.Value.Configuration.Points,
            point => point.PointId.Value == Guid.Parse("82000000-0000-0000-0000-000000000001"));
        Assert.Equal(
            "simulator.activation_stale",
            (await restarted.ActivateAsync(context.ScopeId, revision1, "activator-b")).Error?.Code.Value);

        const string invalidManifest = "{\"simulator\":{\"sourceId\":\"not-a-guid\",\"seed\":1,\"points\":[]}}";
        var delivery3 = Delivery(revision3, 3, invalidManifest);
        Assert.True((await restarted.ReceiveAsync(delivery3, "receiver-a")).IsSuccess);
        var rejected = await restarted.ValidateAsync(context.ScopeId, revision3, "validator-a");
        Assert.Equal(SimulatorManifestStatus.Rejected, rejected.Value.Status);
        Assert.Equal("simulator.manifest_invalid", rejected.Value.RejectionCode);
        Assert.Equal(
            "simulator.activation_not_ready",
            (await restarted.ActivateAsync(context.ScopeId, revision3, "activator-b")).Error?.Code.Value);

        var restored2 = await context.CreateStore().ReadActiveAsync(context.ScopeId);
        Assert.Equal(revision2, restored2.Value.Receipt.RevisionId);
        Assert.Equal(2, restored2.Value.Generation);
    }

    [Fact]
    public async Task DesiredReleaseActivationIsIdempotentAndRollbackCreatesNewActiveRevision()
    {
        await using var context = await SimulatorTestContext.CreateAsync(cluster);
        var manifest = CreateManifest(
            Guid.Parse("83000000-0000-0000-0000-000000000001"),
            31,
            [(Guid.Parse("84000000-0000-0000-0000-000000000001"), 40L)]);
        var dependencies = new[] { new ConfigurationDependency("equipment", "graph-11") };
        var revision1 = await context.PublishAndDistributeAsync(manifest, dependencies, null);
        var activator = context.CreateActivator();

        var active1 = await activator.ActivateDesiredAsync(context.Session, context.ScopeId, "worker-a");
        Assert.Equal(revision1.RevisionId, active1.Value.Receipt.RevisionId);
        Assert.Equal(1, active1.Value.Generation);
        var duplicate = await activator.ActivateDesiredAsync(context.Session, context.ScopeId, "worker-b");
        Assert.Equal(revision1.RevisionId, duplicate.Value.Receipt.RevisionId);
        Assert.Equal(1, duplicate.Value.Generation);

        var beforeRollback = (await context.Configuration.ReadScopeAsync(context.Session, context.ScopeId)).Value;
        var rollback = (await context.Configuration.RollbackAsync(
            context.Session,
            context.ScopeId,
            revision1.RevisionId,
            beforeRollback.Version)).Value;
        Assert.NotEqual(revision1.RevisionId, rollback.RevisionId);
        Assert.Equal(revision1.RevisionId, rollback.SourceRevisionId);
        Assert.Equal(RevisionNumber.From(2), rollback.RevisionNumber);

        var revision2 = await context.PublishAndDistributeAsync(manifest, dependencies, rollback);
        var active2 = await context.CreateActivator().ActivateDesiredAsync(
            context.Session,
            context.ScopeId,
            "worker-c");
        Assert.Equal(revision2.RevisionId, active2.Value.Receipt.RevisionId);
        Assert.Equal(revision1.RevisionId, active2.Value.Receipt.SourceRevisionId);
        Assert.Equal(2, active2.Value.Generation);

        var restarted = await context.CreateStore().ReadActiveAsync(context.ScopeId);
        Assert.Equal(revision2.RevisionId, restarted.Value.Receipt.RevisionId);
        Assert.Equal([40L], restarted.Value.Configuration.Points.Select(point => point.Baseline));
        var configurationState = (await context.Configuration.ReadScopeAsync(context.Session, context.ScopeId)).Value;
        Assert.Equal(revision2.RevisionId, configurationState.ActivatedRevisionId);
    }

    private static SimulatorManifestDelivery Delivery(
        ConfigurationRevisionId revisionId,
        ulong revisionNumber,
        string manifest) =>
        new(
            revisionId,
            FacilityScopeId.From(Guid.Parse("80000000-0000-0000-0000-000000000001")),
            RevisionNumber.From(revisionNumber),
            null,
            manifest,
            Fingerprint(manifest),
            new string('A', 64));

    private static string CreateManifest(Guid sourceId, ulong seed, (Guid PointId, long Baseline)[] points) =>
        JsonSerializer.Serialize(new
        {
            simulator = new
            {
                sourceId,
                seed,
                points = points.Select(point => new
                {
                    pointId = point.PointId,
                    baseline = point.Baseline,
                    amplitude = 0,
                    unit = "kW",
                }),
            },
        });

    private static string Fingerprint(string manifest) =>
        ConfigurationManifestFingerprint.Normalize(manifest).Fingerprint;

    private sealed class SimulatorTestContext : IAsyncDisposable
    {
        private SimulatorTestContext(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            ConfigurationRevisionTests.MutableClock clock,
            FacilityScopeId scopeId,
            SessionSnapshot session)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ScopeId = scopeId;
            Session = session;
            Configuration = CreateConfiguration();
            Store = CreateStore();
        }

        public TestDatabase Database { get; }
        public NpgsqlDataSource DataSource { get; }
        public ConfigurationRevisionTests.MutableClock Clock { get; }
        public FacilityScopeId ScopeId { get; }
        public SessionSnapshot Session { get; }
        public ConfigurationService Configuration { get; }
        public SimulatorRuntimeStore Store { get; }

        public static async Task<SimulatorTestContext> CreateAsync(PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                ConfigurationMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                SimulatorRuntimeMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
            var clock = new ConfigurationRevisionTests.MutableClock(Start);
            var scopeId = FacilityScopeId.From(Guid.Parse("80000000-0000-0000-0000-000000000001"));
            var session = new SessionSnapshot(
                SessionId.New(),
                SubjectId.New(),
                PrincipalKind.User,
                Start.AddMinutes(-1),
                Start.AddHours(2),
                new EffectivePermissions(
                [
                    ConfigurationPermissions.Read(scopeId),
                    ConfigurationPermissions.Save(scopeId),
                    ConfigurationPermissions.Validate(scopeId),
                    ConfigurationPermissions.Publish(scopeId),
                    ConfigurationPermissions.Distribute(scopeId),
                    ConfigurationPermissions.Activate(scopeId),
                ]));
            return new SimulatorTestContext(database, dataSource, clock, scopeId, session);
        }

        public ConfigurationService CreateConfiguration() => new(
            new ConfigurationStore(DataSource, PostgreSqlClusterFixture.OwnerARole, Clock),
            Clock);

        public SimulatorRuntimeStore CreateStore(SimulatorActivationCommitHook? hook = null) => new(
            DataSource,
            PostgreSqlClusterFixture.OwnerBRole,
            Clock,
            hook);

        public SimulatorReleaseActivator CreateActivator() => new(CreateConfiguration(), CreateStore());

        public async Task<ConfigurationRevisionSnapshot> PublishAndDistributeAsync(
            string manifest,
            IReadOnlyCollection<ConfigurationDependency> dependencies,
            ConfigurationRevisionSnapshot? existingDraft)
        {
            var draft = existingDraft ?? (await Configuration.SaveAsync(
                Session,
                ScopeId,
                new SaveConfigurationRequest(manifest, dependencies, null))).Value;
            var validated = (await Configuration.ValidateAsync(
                Session,
                ScopeId,
                draft.RevisionId,
                draft.Version)).Value;
            var published = (await Configuration.PublishAsync(
                Session,
                ScopeId,
                new PublishConfigurationRequest(validated.RevisionId, validated.Version, dependencies))).Value;
            var job = (await Configuration.ClaimDistributionAsync(
                Session,
                ScopeId,
                "distribution-worker",
                TimeSpan.FromMinutes(1))).Value;
            return (await Configuration.CompleteDistributionAsync(
                Session,
                ScopeId,
                job.JobId,
                "distribution-worker")).Value;
        }

        public async Task<long> CountAuditAsync(string action)
        {
            await using var connection = await DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var setRole = new NpgsqlCommand(
                $"SET LOCAL ROLE \"{PostgreSqlClusterFixture.OwnerBRole}\";",
                connection,
                transaction);
            await setRole.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(
                $"SELECT count(*) FROM {SimulatorRuntimeMigrations.Schema}.activation_audit WHERE action = @action;",
                connection,
                transaction);
            command.Parameters.AddWithValue("action", action);
            return (long)(await command.ExecuteScalarAsync() ?? 0L);
        }

        public async Task AssertManifestIsImmutableAsync(ConfigurationRevisionId revisionId)
        {
            await using var connection = await DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var setRole = new NpgsqlCommand(
                $"SET LOCAL ROLE \"{PostgreSqlClusterFixture.OwnerBRole}\";",
                connection,
                transaction);
            await setRole.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(
                $"UPDATE {SimulatorRuntimeMigrations.Schema}.manifest SET manifest = '{{}}' WHERE revision_id = @revision_id;",
                connection,
                transaction);
            command.Parameters.AddWithValue("revision_id", revisionId.Value);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("immutable", exception.MessageText, StringComparison.Ordinal);
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}
