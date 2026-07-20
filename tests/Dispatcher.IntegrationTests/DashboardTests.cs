using Dispatcher.Core;
using Dispatcher.Dashboards;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Server;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class DashboardTests(PostgreSqlClusterFixture cluster)
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PublishedRevisionIsWholeImmutableAndHasExactDependencies()
    {
        await using var context = await DashboardTestContext.CreateAsync(cluster);
        var revision = context.Revision(context.FirstDashboardId, "Operations", includeHiddenBinding: true);

        var published = await context.Store.PublishAsync(revision);
        var restartedStore = new DashboardStore(
            context.DataSource, PostgreSqlClusterFixture.OwnerARole, context.Clock);
        var restored = await restartedStore.ReadPublishedAsync(context.FirstDashboardId);

        Assert.NotNull(restored);
        Assert.Equal(revision.RevisionId, restored.Revision.RevisionId);
        Assert.Equal(2, restored.Revision.Windows[0].Bindings.Count);
        Assert.Equal(2, restored.Revision.Dependencies.Count);
        Assert.Equal(published.ManifestFingerprint, restored.ManifestFingerprint);
        Assert.Equal(published.DependencyFingerprint, restored.DependencyFingerprint);

        await using var mutation = context.DataSource.CreateCommand($"""
            UPDATE {DashboardMigrations.Schema}.published_revision
            SET manifest = jsonb_build_object()
            WHERE revision_id = @revision_id;
            """);
        mutation.Parameters.AddWithValue("revision_id", revision.RevisionId.Value);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => mutation.ExecuteNonQueryAsync());
        Assert.Contains("immutable", exception.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthorizationFiltersCatalogManifestAndFallsBackAfterAccessLoss()
    {
        await using var context = await DashboardTestContext.CreateAsync(cluster);
        await context.Store.PublishAsync(context.Revision(
            context.FirstDashboardId, "First", includeHiddenBinding: true));
        await context.Store.PublishAsync(context.Revision(
            context.SecondDashboardId, "Second", includeHiddenBinding: false));
        var service = new AuthorizedDashboardService(context.Store, context.Clock);
        var fullSession = context.Session(readFirst: true, readSecond: true);

        var manifest = await service.ReadManifestAsync(
            fullSession, context.FirstDashboardId, CancellationToken.None);
        var window = Assert.Single(manifest.Value.Windows);
        Assert.Single(window.Bindings);
        Assert.Single(window.Widgets);
        Assert.Single(window.Widgets[0].BindingIds);
        Assert.Single(manifest.Value.Dependencies);
        Assert.DoesNotContain(window.Bindings, binding => binding.PointId == context.HiddenPointId);
        Assert.DoesNotContain(manifest.Value.Dependencies, dependency =>
            dependency.BindingId == context.HiddenBindingId);

        await service.RecordOpenedAsync(fullSession, context.FirstDashboardId, CancellationToken.None);
        await service.SetFavoriteAsync(fullSession, context.SecondDashboardId, true, CancellationToken.None);
        var restricted = context.Session(readFirst: false, readSecond: true);
        var catalog = await service.ReadCatalogAsync(restricted, CancellationToken.None);
        Assert.Equal(context.SecondDashboardId, Assert.Single(catalog.Value).DashboardId);

        var fallback = await service.ResolveLandingAsync(restricted, CancellationToken.None);
        Assert.Equal(context.SecondDashboardId, fallback.Value);
        Assert.Equal(context.SecondDashboardId, await context.Store.ReadLastDashboardAsync(restricted.SubjectId));
    }

    private sealed class DashboardTestContext : IAsyncDisposable
    {
        private DashboardTestContext(TestDatabase database, NpgsqlDataSource dataSource)
        {
            Database = database;
            DataSource = dataSource;
            Clock = new FixedClock(Now);
            Store = new DashboardStore(dataSource, PostgreSqlClusterFixture.OwnerARole, Clock);
        }

        public TestDatabase Database { get; }
        public NpgsqlDataSource DataSource { get; }
        public FixedClock Clock { get; }
        public DashboardStore Store { get; }
        public DashboardId FirstDashboardId { get; } = DashboardId.From(
            Guid.Parse("d1000000-0000-7000-8000-000000000001"));
        public DashboardId SecondDashboardId { get; } = DashboardId.From(
            Guid.Parse("d1000000-0000-7000-8000-000000000002"));
        public RuntimeScopeId ScopeId { get; } = RuntimeScopeId.From(
            Guid.Parse("d2000000-0000-7000-8000-000000000001"));
        public PointId AllowedPointId { get; } = PointId.From(
            Guid.Parse("d3000000-0000-7000-8000-000000000001"));
        public PointId HiddenPointId { get; } = PointId.From(
            Guid.Parse("d3000000-0000-7000-8000-000000000002"));
        public DashboardBindingId AllowedBindingId { get; } = DashboardBindingId.From(
            Guid.Parse("d4000000-0000-7000-8000-000000000001"));
        public DashboardBindingId HiddenBindingId { get; } = DashboardBindingId.From(
            Guid.Parse("d4000000-0000-7000-8000-000000000002"));

        public static async Task<DashboardTestContext> CreateAsync(PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                DashboardMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
            return new DashboardTestContext(database, dataSource);
        }

        public DashboardRevision Revision(DashboardId dashboardId, string name, bool includeHiddenBinding)
        {
            var bindings = new List<DashboardBinding>
            {
                new(AllowedBindingId, DashboardBindingSource.Current, ScopeId, AllowedPointId,
                    RuntimePermissions.ReadPoint(AllowedPointId)),
            };
            if (includeHiddenBinding)
            {
                bindings.Add(new DashboardBinding(
                    HiddenBindingId, DashboardBindingSource.Alarm, ScopeId, HiddenPointId,
                    RuntimePermissions.ReadPoint(HiddenPointId)));
            }

            var bindingIds = bindings.Select(item => item.BindingId).ToArray();
            return new DashboardRevision(
                dashboardId,
                DashboardRevisionId.New(),
                1,
                name,
                null,
                [new DashboardWindow(
                    DashboardWindowId.New(),
                    "Main",
                    [new Widget(WidgetId.New(), "value", "Process value", bindingIds)],
                    bindings)],
                bindings.Select(binding => new DashboardDependency(
                    binding.BindingId,
                    $"point:{binding.PointId.Value:N}",
                    $"revision:{binding.PointId.Value:N}")).ToArray(),
                Now);
        }

        public SessionSnapshot Session(bool readFirst, bool readSecond)
        {
            var permissions = new List<PermissionCode>
            {
                DashboardPermissions.CatalogRead,
                DashboardPermissions.Personalize,
                RuntimePermissions.ReadPoint(AllowedPointId),
            };
            if (readFirst)
            {
                permissions.Add(DashboardPermissions.Read(FirstDashboardId));
            }

            if (readSecond)
            {
                permissions.Add(DashboardPermissions.Read(SecondDashboardId));
            }

            return new SessionSnapshot(
                SessionId.New(),
                SubjectId.From(Guid.Parse("d5000000-0000-7000-8000-000000000001")),
                PrincipalKind.User,
                Now.AddMinutes(-1),
                Now.AddHours(1),
                new EffectivePermissions(permissions));
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IWallClock
    {
        public DateTimeOffset GetUtcNow() => now;
    }
}
