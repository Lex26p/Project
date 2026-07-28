using Dispatcher.Configuration;
using Dispatcher.Facilities;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class ConfigurationRevisionTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 19, 8, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public ConfigurationRevisionTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task SaveValidatePublishDistributeAndActivateRemainDistinct()
    {
        await using var context = await ConfigurationTestContext.CreateAsync(cluster);
        var dependencyA = new[] { new ConfigurationDependency("equipment", "version-1") };
        var dependencyB = new[] { new ConfigurationDependency("equipment", "version-2") };

        var draft = await context.Service.SaveAsync(
            context.Session,
            context.ScopeId,
            new SaveConfigurationRequest("{\"mode\":\"initial\"}", dependencyA, null));
        Assert.True(draft.IsSuccess);
        Assert.Null(draft.Value.ValidatedAt);
        Assert.Equal(
            "configuration.release_not_found",
            (await context.Service.ReadDesiredReleaseAsync(context.Session, context.ScopeId)).Error?.Code.Value);

        var validated = await context.Service.ValidateAsync(
            context.Session,
            context.ScopeId,
            draft.Value.RevisionId,
            draft.Value.Version);
        var changed = await context.Service.SaveAsync(
            context.Session,
            context.ScopeId,
            new SaveConfigurationRequest(
                "{\"mode\":\"changed-after-validation\"}",
                dependencyA,
                validated.Value.Version));
        Assert.Null(changed.Value.ValidatedAt);
        Assert.Equal(
            "configuration.validation_stale",
            (await context.Service.PublishAsync(
                context.Session,
                context.ScopeId,
                new PublishConfigurationRequest(changed.Value.RevisionId, changed.Value.Version, dependencyA)))
            .Error?.Code.Value);

        var revalidated = await context.Service.ValidateAsync(
            context.Session,
            context.ScopeId,
            changed.Value.RevisionId,
            changed.Value.Version);
        Assert.Equal(
            "configuration.validation_stale",
            (await context.Service.PublishAsync(
                context.Session,
                context.ScopeId,
                new PublishConfigurationRequest(revalidated.Value.RevisionId, revalidated.Value.Version, dependencyB)))
            .Error?.Code.Value);

        var published = await context.Service.PublishAsync(
            context.Session,
            context.ScopeId,
            new PublishConfigurationRequest(revalidated.Value.RevisionId, revalidated.Value.Version, dependencyA));
        Assert.NotNull(published.Value.PublishedAt);
        Assert.Null(published.Value.DistributedAt);
        Assert.Null(published.Value.ActivatedAt);
        Assert.Equal(
            "configuration.workload_activation_not_found",
            (await context.Deployments.ReadActivatedAsync(context.ScopeId)).Error?.Code.Value);

        var desired = await context.Service.ReadDesiredReleaseAsync(context.Session, context.ScopeId);
        Assert.Equal(published.Value.RevisionId, desired.Value.RevisionId);
        Assert.Null(desired.Value.DistributedAt);

        var job = await context.Deployments.ClaimNextAsync(
            context.ScopeId,
            "worker-a",
            TimeSpan.FromMinutes(1));
        var distributed = await context.Deployments.MarkPreparedAsync(job.Value);
        Assert.NotNull(distributed.Value.Revision.DistributedAt);
        Assert.Null(distributed.Value.Revision.ActivatedAt);
        Assert.Equal(
            "configuration.workload_not_switched",
            (await context.Deployments.AcknowledgeAsync(distributed.Value)).Error?.Code.Value);
        var switched = await context.Deployments.RecordSwitchAsync(
            distributed.Value,
            1,
            RevisionNumber.Initial);
        var activated = await context.Deployments.AcknowledgeAsync(switched.Value);
        Assert.NotNull(activated.Value.Revision.ActivatedAt);

        var restored = context.CreateService();
        var state = await restored.ReadScopeAsync(context.Session, context.ScopeId);
        Assert.Equal(activated.Value.Revision.RevisionId, state.Value.PublishedRevisionId);
        Assert.Equal(activated.Value.Revision.RevisionId, state.Value.DistributedRevisionId);
        Assert.Equal(activated.Value.Revision.RevisionId, state.Value.ActivatedRevisionId);
        Assert.Equal(activated.Value.Revision.Version, Assert.Single(state.Value.Revisions).Version);
        Assert.Equal(5L, await context.CountAuditAsync());
    }

    [Fact]
    public async Task RevisionRacesLeaseRecoveryAndRollbackCreateNoMixedRelease()
    {
        await using var context = await ConfigurationTestContext.CreateAsync(cluster);
        var dependencies = new[] { new ConfigurationDependency("facility", "graph-1") };
        var draft = (await context.Service.SaveAsync(
            context.Session,
            context.ScopeId,
            new SaveConfigurationRequest("{\"generation\":1}", dependencies, null))).Value;
        Assert.Equal(
            "configuration.version_conflict",
            (await context.Service.SaveAsync(
                context.Session,
                context.ScopeId,
                new SaveConfigurationRequest("{\"generation\":2}", dependencies, draft.Version - 1)))
            .Error?.Code.Value);
        var validated = (await context.Service.ValidateAsync(
            context.Session,
            context.ScopeId,
            draft.RevisionId,
            draft.Version)).Value;
        _ = (await context.Service.PublishAsync(
            context.Session,
            context.ScopeId,
            new PublishConfigurationRequest(validated.RevisionId, validated.Version, dependencies))).Value;

        var firstClaim = await context.Deployments.ClaimNextAsync(
            context.ScopeId,
            "worker-a",
            TimeSpan.FromSeconds(30));
        context.Clock.Advance(TimeSpan.FromSeconds(31));
        var restarted = context.CreateService();
        var restartedDeployments = context.CreateDeployments();
        var recoveredClaim = await restartedDeployments.ClaimNextAsync(
            context.ScopeId,
            "worker-b",
            TimeSpan.FromSeconds(30));
        Assert.Equal(firstClaim.Value.JobId, recoveredClaim.Value.JobId);
        Assert.Equal(2, recoveredClaim.Value.Attempts);
        Assert.Equal(
            "configuration.workload_lease_invalid",
            (await restartedDeployments.MarkPreparedAsync(firstClaim.Value)).Error?.Code.Value);
        var distributed = (await restartedDeployments.MarkPreparedAsync(recoveredClaim.Value)).Value;
        var switched = (await restartedDeployments.RecordSwitchAsync(
            distributed,
            1,
            RevisionNumber.Initial)).Value;
        var activated = (await restartedDeployments.AcknowledgeAsync(switched)).Value;

        var beforeRollback = (await restarted.ReadScopeAsync(context.Session, context.ScopeId)).Value;
        var rollback = await restarted.RollbackAsync(
            context.Session,
            context.ScopeId,
            activated.Revision.RevisionId,
            beforeRollback.Version);
        Assert.NotEqual(activated.Revision.RevisionId, rollback.Value.RevisionId);
        Assert.Equal(activated.Revision.RevisionId, rollback.Value.SourceRevisionId);
        Assert.Equal(RevisionNumber.From(2), rollback.Value.RevisionNumber);
        Assert.Null(rollback.Value.ValidatedAt);
        Assert.Null(rollback.Value.PublishedAt);

        var afterRollback = (await restarted.ReadScopeAsync(context.Session, context.ScopeId)).Value;
        Assert.Equal(rollback.Value.RevisionId, afterRollback.DraftRevisionId);
        Assert.Equal(activated.Revision.RevisionId, afterRollback.PublishedRevisionId);
        Assert.Equal(activated.Revision.RevisionId, afterRollback.ActivatedRevisionId);
        Assert.Equal(
            activated.Revision.RevisionId,
            (await restarted.ReadDesiredReleaseAsync(context.Session, context.ScopeId)).Value.RevisionId);
    }

    [Fact]
    public async Task ScopedPermissionsFailClosedWithoutCreatingMetadata()
    {
        await using var context = await ConfigurationTestContext.CreateAsync(cluster);
        var otherScope = FacilityScopeId.From(Guid.Parse("71000000-0000-0000-0000-000000000099"));
        var denied = await context.Service.SaveAsync(
            context.Session,
            otherScope,
            new SaveConfigurationRequest("{}", [], null));
        Assert.Equal("permission.denied", denied.Error?.Code.Value);
        Assert.Equal(0L, await context.CountScopeAsync(otherScope));
    }

    private sealed class ConfigurationTestContext : IAsyncDisposable
    {
        private ConfigurationTestContext(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            MutableClock clock,
            FacilityScopeId scopeId,
            SessionSnapshot session)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ScopeId = scopeId;
            Session = session;
            Service = CreateService();
            Deployments = CreateDeployments();
        }

        public TestDatabase Database { get; }
        public NpgsqlDataSource DataSource { get; }
        public MutableClock Clock { get; }
        public FacilityScopeId ScopeId { get; }
        public SessionSnapshot Session { get; }
        public ConfigurationService Service { get; }
        public ConfigurationWorkloadDeploymentStore Deployments { get; }

        public static async Task<ConfigurationTestContext> CreateAsync(PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                ConfigurationMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
            var clock = new MutableClock(Start);
            var scopeId = FacilityScopeId.From(Guid.Parse("71000000-0000-0000-0000-000000000001"));
            var permissions = new[]
            {
                ConfigurationPermissions.Read(scopeId),
                ConfigurationPermissions.Save(scopeId),
                ConfigurationPermissions.Validate(scopeId),
                ConfigurationPermissions.Publish(scopeId),
            };
            var session = new SessionSnapshot(
                SessionId.New(),
                SubjectId.New(),
                PrincipalKind.User,
                Start.AddMinutes(-1),
                Start.AddHours(2),
                new EffectivePermissions(permissions));
            return new ConfigurationTestContext(database, dataSource, clock, scopeId, session);
        }

        public ConfigurationService CreateService() => new(
            new ConfigurationStore(DataSource, PostgreSqlClusterFixture.OwnerARole, Clock),
            Clock);

        public ConfigurationWorkloadDeploymentStore CreateDeployments() => new(
            DataSource,
            PostgreSqlClusterFixture.OwnerARole,
            Clock);

        public async Task<long> CountAuditAsync()
        {
            await using var command = DataSource.CreateCommand(
                $"SELECT count(*) FROM {ConfigurationMigrations.Schema}.mutation_audit;");
            return (long)(await command.ExecuteScalarAsync() ?? 0L);
        }

        public async Task<long> CountScopeAsync(FacilityScopeId scopeId)
        {
            await using var command = DataSource.CreateCommand(
                $"SELECT count(*) FROM {ConfigurationMigrations.Schema}.scope_state WHERE scope_id = @scope_id;");
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            return (long)(await command.ExecuteScalarAsync() ?? 0L);
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    public sealed class MutableClock : IWallClock
    {
        private DateTimeOffset now;

        public MutableClock(DateTimeOffset now)
        {
            this.now = now;
        }

        public DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
