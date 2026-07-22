using Dispatcher.Administration;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class AdministrationOperationsTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly PermissionCode VisibleScope = PermissionCode.From("operations.scope-visible.read");
    private static readonly PermissionCode HiddenScope = PermissionCode.From("operations.scope-hidden.read");
    private readonly PostgreSqlClusterFixture cluster;

    public AdministrationOperationsTests(PostgreSqlClusterFixture cluster) => this.cluster = cluster;

    [Fact]
    public async Task OperationalViewsAreOwnerLinkedPermissionFilteredImmutableAndBounded()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource, AdministrationMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        var store = new AdministrationStore(
            dataSource, PostgreSqlClusterFixture.OwnerARole,
            new AdministrationQueryLimits(maximumViewItems: 10, maximumAuditPageSize: 10, retainedAuditTail: 3));
        var session = Session(
            AdministrationPermissions.ReadHealth,
            AdministrationPermissions.ReadDataQuality,
            AdministrationPermissions.ReadAudit,
            VisibleScope);
        var healthAccess = SessionAuthorization.AuthorizeAccess(session, AdministrationPermissions.ReadHealth, new Clock()).Value;
        var qualityAccess = SessionAuthorization.AuthorizeAccess(session, AdministrationPermissions.ReadDataQuality, new Clock()).Value;
        var auditAccess = SessionAuthorization.AuthorizeAccess(session, AdministrationPermissions.ReadAudit, new Clock()).Value;
        var visibleScopeId = Guid.NewGuid();
        var hiddenScopeId = Guid.NewGuid();

        var visibleHealth = new OwnerHealthFact(
            Guid.NewGuid(), "MOD-RTM", visibleScopeId, VisibleScope,
            OperationalHealthFacet.ProtectedContinuity, OperationalHealthState.Degraded,
            "source_gap", Start);
        Assert.True((await store.AcceptHealthAsync(visibleHealth)).IsSuccess);
        Assert.True((await store.AcceptHealthAsync(visibleHealth)).IsSuccess);
        Assert.Equal("administration.fact_conflict", (await store.AcceptHealthAsync(
            visibleHealth with { State = OperationalHealthState.Healthy })).Error?.Code.Value);
        Assert.True((await store.AcceptHealthAsync(new(
            Guid.NewGuid(), "MOD-RTM", hiddenScopeId, HiddenScope,
            OperationalHealthFacet.Persistence, OperationalHealthState.Unavailable,
            "database_unavailable", Start))).IsSuccess);

        var health = await store.ReadHealthAsync(healthAccess);
        var onlyVisibleHealth = Assert.Single(health.Value.Items);
        Assert.Equal(visibleScopeId, onlyVisibleHealth.ScopeId);
        Assert.Equal(1, health.Value.Degraded);
        Assert.Equal(0, health.Value.Unavailable);

        var resolvedIssue = Guid.NewGuid();
        Assert.True((await store.AcceptDataQualityAsync(new(
            Guid.NewGuid(), resolvedIssue, "MOD-RTM", visibleScopeId, VisibleScope,
            DataQualityIssueKind.Stale, OperationalSeverity.Warning, DataQualityIssueState.Open,
            "Source value is stale.", "point/visible", Start))).IsSuccess);
        Assert.True((await store.AcceptDataQualityAsync(new(
            Guid.NewGuid(), Guid.NewGuid(), "MOD-RTM", hiddenScopeId, HiddenScope,
            DataQualityIssueKind.SourceGap, OperationalSeverity.Critical, DataQualityIssueState.Open,
            "Protected source gap.", "point/hidden", Start))).IsSuccess);
        Assert.True((await store.AcceptDataQualityAsync(new(
            Guid.NewGuid(), resolvedIssue, "MOD-RTM", visibleScopeId, VisibleScope,
            DataQualityIssueKind.Stale, OperationalSeverity.Warning, DataQualityIssueState.Resolved,
            "Source value recovered.", "point/visible", Start.AddSeconds(1)))).IsSuccess);
        var openIssueId = Guid.NewGuid();
        Assert.True((await store.AcceptDataQualityAsync(new(
            Guid.NewGuid(), openIssueId, "MOD-EQP", visibleScopeId, VisibleScope,
            DataQualityIssueKind.Configuration, OperationalSeverity.Warning, DataQualityIssueState.Open,
            "Published definition is incomplete.", "equipment/visible", Start.AddSeconds(2)))).IsSuccess);

        var quality = await store.ReadDataQualityAsync(qualityAccess);
        Assert.Equal(openIssueId, Assert.Single(quality.Value.Issues).IssueId);
        Assert.Equal(1, quality.Value.Warning);
        Assert.Equal(0, quality.Value.Critical);
        Assert.DoesNotContain(
            typeof(AdministrationStore).Assembly.GetReferencedAssemblies(),
            reference => reference.Name == "Dispatcher.Alarm");

        var firstVisibleAudit = Audit(Guid.NewGuid(), visibleScopeId, VisibleScope, "publish", Start);
        var hiddenAudit = Audit(Guid.NewGuid(), hiddenScopeId, HiddenScope, "hidden-change", Start.AddSeconds(1));
        var secondVisibleAudit = Audit(Guid.NewGuid(), visibleScopeId, VisibleScope, "activate", Start.AddSeconds(2));
        Assert.True((await store.AcceptAuditAsync(firstVisibleAudit)).IsSuccess);
        Assert.True((await store.AcceptAuditAsync(hiddenAudit)).IsSuccess);
        Assert.True((await store.AcceptAuditAsync(secondVisibleAudit)).IsSuccess);
        Assert.True((await store.AcceptAuditAsync(firstVisibleAudit)).IsSuccess);
        Assert.Equal("administration.audit_conflict", (await store.AcceptAuditAsync(
            firstVisibleAudit with { Outcome = AuditOutcome.Rejected })).Error?.Code.Value);

        var initial = await store.ReadAuditAsync(auditAccess, new(null, 10));
        Assert.Equal(AuditFeedKind.Records, initial.Value.Kind);
        Assert.Equal(["publish", "activate"], initial.Value.Records.Select(value => value.Action));
        Assert.DoesNotContain(initial.Value.Records, value => value.ScopeId == hiddenScopeId);
        var oldCursor = initial.Value.Cursor;
        var live = Audit(Guid.NewGuid(), visibleScopeId, VisibleScope, "live-tail", Start.AddSeconds(3));
        Assert.True((await store.AcceptAuditAsync(live)).IsSuccess);
        var tail = await store.ReadAuditAsync(auditAccess, new(oldCursor, 10));
        Assert.Equal("live-tail", Assert.Single(tail.Value.Records).Action);

        for (var index = 0; index < 4; index++)
            Assert.True((await store.AcceptAuditAsync(Audit(
                Guid.NewGuid(), visibleScopeId, VisibleScope, $"overload-{index}", Start.AddSeconds(4 + index)))).IsSuccess);
        var gap = await store.ReadAuditAsync(auditAccess, new(oldCursor, 10));
        Assert.Equal(AuditFeedKind.Gap, gap.Value.Kind);
        Assert.Empty(gap.Value.Records);
        Assert.Null(gap.Value.Cursor);
        Assert.Equal("administration.audit_page_invalid", (await store.ReadAuditAsync(
            auditAccess, new(null, 11))).Error?.Code.Value);

        var restarted = new AdministrationStore(
            dataSource, PostgreSqlClusterFixture.OwnerARole,
            new AdministrationQueryLimits(maximumViewItems: 10, maximumAuditPageSize: 10, retainedAuditTail: 3));
        Assert.Equal(openIssueId, Assert.Single((await restarted.ReadDataQualityAsync(qualityAccess)).Value.Issues).IssueId);
        var wrongAccess = SessionAuthorization.AuthorizeAccess(session, AdministrationPermissions.ReadHealth, new Clock()).Value;
        Assert.Equal("permission.denied", (await restarted.ReadAuditAsync(wrongAccess, new(null, 10))).Error?.Code.Value);

        Assert.True((await store.AcceptHealthAsync(new(
            Guid.NewGuid(), "MOD-RTM", visibleScopeId, VisibleScope,
            OperationalHealthFacet.Capacity, OperationalHealthState.Healthy,
            "within_bound", Start.AddSeconds(8)))).IsSuccess);
        var bounded = new AdministrationStore(
            dataSource, PostgreSqlClusterFixture.OwnerARole,
            new AdministrationQueryLimits(maximumViewItems: 1, maximumAuditPageSize: 10, retainedAuditTail: 3));
        Assert.Equal("administration.view_overload", (await bounded.ReadHealthAsync(healthAccess)).Error?.Code.Value);

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var mutation = new NpgsqlCommand(
            "UPDATE administration.audit_record SET summary='mutated' WHERE event_id=@id;", connection);
        mutation.Parameters.AddWithValue("id", firstVisibleAudit.EventId);
        await Assert.ThrowsAsync<PostgresException>(() => mutation.ExecuteNonQueryAsync());
    }

    private static OwnerAuditFact Audit(
        Guid id, Guid scopeId, PermissionCode permission, string action, DateTimeOffset occurredAt) =>
        new(id, "MOD-RLS", scopeId, permission, SessionId.New(), SubjectId.New(), PrincipalKind.User,
            action, "configuration-revision", Guid.NewGuid(), AuditOutcome.Succeeded,
            "Authorized owner mutation.", occurredAt);

    private static SessionSnapshot Session(params PermissionCode[] permissions) => new(
        SessionId.New(), SubjectId.New(), PrincipalKind.User,
        Start.AddMinutes(-1), Start.AddHours(1), new EffectivePermissions(permissions));

    private sealed class Clock : Dispatcher.Semantics.IWallClock
    {
        public DateTimeOffset GetUtcNow() => Start;
    }
}
