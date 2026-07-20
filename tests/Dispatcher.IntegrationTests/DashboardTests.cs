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

    [Fact]
    public async Task SubscriptionContainsOnlyVisibleAuthorizedConsumerLinksAndEnforcesCapacity()
    {
        await using var context = await DashboardTestContext.CreateAsync(cluster);
        var revision = context.LinkedRevision();
        await context.Store.PublishAsync(revision);
        var authorized = new AuthorizedDashboardService(context.Store, context.Clock);
        var generations = new DashboardSubscriptionGenerationStore();
        var subscriptions = new DashboardSubscriptionService(
            authorized,
            new DashboardRuntimeLimits(maxVisibleWindows: 1, maxBindings: 2),
            generations);
        var visibleWindow = revision.Windows[0].WindowId;

        var result = await subscriptions.CreateAsync(
            context.LinkedSession(includeAlarm: true, includeHistory: true),
            context.FirstDashboardId,
            [visibleWindow],
            CancellationToken.None);
        Assert.Equal(2, result.Value.Links.Count);
        Assert.Contains(result.Value.Links, link => link.Source == "Current" && link.Endpoint == "/hubs/runtime");
        Assert.Contains(result.Value.Links, link => link.Source == "Alarm" && link.Endpoint == "/hubs/events");
        Assert.DoesNotContain(result.Value.Links, link => link.Source == "History");

        var restricted = await subscriptions.CreateAsync(
            context.LinkedSession(includeAlarm: false, includeHistory: true),
            context.FirstDashboardId,
            [visibleWindow],
            CancellationToken.None);
        Assert.Equal("Current", Assert.Single(restricted.Value.Links).Source);

        var tooManyWindows = await subscriptions.CreateAsync(
            context.LinkedSession(includeAlarm: true, includeHistory: true),
            context.FirstDashboardId,
            revision.Windows.Select(window => window.WindowId).ToArray(),
            CancellationToken.None);
        Assert.Equal("dashboard.visible_window_capacity", tooManyWindows.Error?.Code.Value);

        var bindingLimited = new DashboardSubscriptionService(
            authorized,
            new DashboardRuntimeLimits(maxVisibleWindows: 2, maxBindings: 2),
            generations);
        var tooManyBindings = await bindingLimited.CreateAsync(
            context.LinkedSession(includeAlarm: true, includeHistory: true),
            context.FirstDashboardId,
            revision.Windows.Select(window => window.WindowId).ToArray(),
            CancellationToken.None);
        Assert.Equal("dashboard.binding_capacity", tooManyBindings.Error?.Code.Value);
    }

    [Fact]
    public async Task DashboardEditorRequiresExactValidationAndPublishesAtomicallyWithGenerationInvalidation()
    {
        await using var context = await DashboardTestContext.CreateAsync(cluster);
        var generations = new DashboardSubscriptionGenerationStore();
        var authoring = new DashboardAuthoringService(
            context.Store,
            context.SvgLimits,
            context.Clock,
            generations);
        var editor = context.DashboardEditorSession();
        var denied = await authoring.SaveDashboardAsync(
            context.Session(readFirst: true, readSecond: false),
            context.FirstDashboardId,
            new SaveDashboardDraftRequest(context.Draft("Denied"), null),
            CancellationToken.None);
        Assert.Equal("permission.denied", denied.Error?.Code.Value);

        var first = await authoring.SaveDashboardAsync(
            editor,
            context.FirstDashboardId,
            new SaveDashboardDraftRequest(context.Draft("First"), null),
            CancellationToken.None);
        var validated = await authoring.ValidateDashboardAsync(
            editor, context.FirstDashboardId, first.Value.RevisionId, first.Value.Version, CancellationToken.None);
        var changed = await authoring.SaveDashboardAsync(
            editor,
            context.FirstDashboardId,
            new SaveDashboardDraftRequest(context.Draft("Changed"), validated.Value.Version),
            CancellationToken.None);
        var stale = await authoring.PublishDashboardAsync(
            editor,
            context.FirstDashboardId,
            new PublishAuthoringRequest(changed.Value.RevisionId, changed.Value.Version),
            CancellationToken.None);
        Assert.Equal("dashboard.validation_stale", stale.Error?.Code.Value);
        Assert.Null(await context.Store.ReadPublishedAsync(context.FirstDashboardId));

        validated = await authoring.ValidateDashboardAsync(
            editor, context.FirstDashboardId, changed.Value.RevisionId, changed.Value.Version, CancellationToken.None);
        var publishedFirst = await authoring.PublishDashboardAsync(
            editor,
            context.FirstDashboardId,
            new PublishAuthoringRequest(validated.Value.RevisionId, validated.Value.Version),
            CancellationToken.None);
        var runtimeFirst = await context.Store.ReadPublishedAsync(context.FirstDashboardId);
        Assert.Equal(publishedFirst.Value.RevisionId, runtimeFirst?.Revision.RevisionId.Value);
        Assert.Equal("Changed", runtimeFirst?.Revision.Name);

        var subscriptions = new DashboardSubscriptionService(
            new AuthorizedDashboardService(context.Store, context.Clock),
            new DashboardRuntimeLimits(2, 8),
            generations);
        var viewer = context.Session(readFirst: true, readSecond: false);
        var generation = await subscriptions.CreateAsync(
            viewer,
            context.FirstDashboardId,
            [runtimeFirst!.Revision.Windows[0].WindowId],
            CancellationToken.None);
        Assert.True(subscriptions.IsCurrent(viewer, generation.Value.SubscriptionId));

        var second = await authoring.SaveDashboardAsync(
            editor,
            context.FirstDashboardId,
            new SaveDashboardDraftRequest(context.Draft("Second"), null),
            CancellationToken.None);
        var secondValidated = await authoring.ValidateDashboardAsync(
            editor, context.FirstDashboardId, second.Value.RevisionId, second.Value.Version, CancellationToken.None);
        var publishedSecond = await authoring.PublishDashboardAsync(
            editor,
            context.FirstDashboardId,
            new PublishAuthoringRequest(secondValidated.Value.RevisionId, secondValidated.Value.Version),
            CancellationToken.None);
        var runtimeSecond = await context.Store.ReadPublishedAsync(context.FirstDashboardId);
        Assert.Equal(publishedSecond.Value.RevisionId, runtimeSecond?.Revision.RevisionId.Value);
        Assert.Equal("Second", runtimeSecond?.Revision.Name);
        Assert.False(subscriptions.IsCurrent(viewer, generation.Value.SubscriptionId));

        var state = await context.Store.ReadAuthoringStateAsync(context.FirstDashboardId.Value);
        var rollback = await authoring.RollbackDashboardAsync(
            editor,
            context.FirstDashboardId,
            publishedFirst.Value.RevisionId,
            state!.Version,
            CancellationToken.None);
        Assert.Equal(publishedFirst.Value.RevisionId, rollback.Value.SourceRevisionId);
        Assert.Null(rollback.Value.ValidatedAt);
        Assert.Equal(publishedSecond.Value.RevisionId,
            (await context.Store.ReadPublishedAsync(context.FirstDashboardId))?.Revision.RevisionId.Value);

        await using var audit = context.DataSource.CreateCommand($"""
            SELECT count(*) FROM {DashboardMigrations.Schema}.editor_audit
            WHERE resource_id = @resource_id;
            """);
        audit.Parameters.AddWithValue("resource_id", context.FirstDashboardId.Value);
        Assert.Equal(9L, (long)(await audit.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task MimicEditorRejectsUnsafeSvgAndPublishesOnlySanitizedExactBindings()
    {
        await using var context = await DashboardTestContext.CreateAsync(cluster);
        var generations = new DashboardSubscriptionGenerationStore();
        var authoring = new DashboardAuthoringService(
            context.Store,
            context.SvgLimits,
            context.Clock,
            generations);
        var mimicId = MimicId.From(Guid.Parse("d7000000-0000-7000-8000-000000000001"));
        var editor = DashboardTestContext.MimicEditorSession(mimicId);
        var malicious = context.Mimic("""
            <svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>
            """);
        var rejected = await authoring.SaveMimicAsync(
            editor,
            mimicId,
            new SaveMimicDraftRequest(malicious, null),
            CancellationToken.None);
        Assert.Equal("dashboard.content_invalid", rejected.Error?.Code.Value);

        var oversized = await authoring.SaveMimicAsync(
            editor,
            mimicId,
            new SaveMimicDraftRequest(context.Mimic(
                $"<svg xmlns=\"http://www.w3.org/2000/svg\">{new string(' ', 5000)}</svg>"), null),
            CancellationToken.None);
        Assert.Equal("dashboard.content_invalid", oversized.Error?.Code.Value);

        var mismatched = await authoring.SaveMimicAsync(
            editor,
            mimicId,
            new SaveMimicDraftRequest(context.Mimic(
                "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect /></svg>"), null),
            CancellationToken.None);
        Assert.Equal("dashboard.content_invalid", mismatched.Error?.Code.Value);

        var safe = context.Mimic($"""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10">
              <rect x="1" y="1" width="8" height="8" data-binding-id="{context.AllowedBindingId.Value:D}" />
            </svg>
            """);
        var saved = await authoring.SaveMimicAsync(
            editor,
            mimicId,
            new SaveMimicDraftRequest(safe, null),
            CancellationToken.None);
        var validated = await authoring.ValidateMimicAsync(
            editor, mimicId, saved.Value.RevisionId, saved.Value.Version, CancellationToken.None);
        var published = await authoring.PublishMimicAsync(
            editor,
            mimicId,
            new PublishAuthoringRequest(validated.Value.RevisionId, validated.Value.Version),
            CancellationToken.None);
        var state = await context.Store.ReadAuthoringStateAsync(mimicId.Value);
        Assert.Equal(published.Value.RevisionId, state?.PublishedRevisionId);
        Assert.Null(state?.DraftRevisionId);

        await using var mutation = context.DataSource.CreateCommand($"""
            UPDATE {DashboardMigrations.Schema}.authoring_revision
            SET content = jsonb_build_object()
            WHERE revision_id = @revision_id;
            """);
        mutation.Parameters.AddWithValue("revision_id", published.Value.RevisionId);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => mutation.ExecuteNonQueryAsync());
        Assert.Contains("immutable", exception.MessageText, StringComparison.Ordinal);
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
        public SvgIntakeLimits SvgLimits { get; } = new(4096, 32, 16, 256);
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
        public DashboardBindingId HistoryBindingId { get; } = DashboardBindingId.From(
            Guid.Parse("d4000000-0000-7000-8000-000000000003"));

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

        public DashboardRevision LinkedRevision()
        {
            var current = new DashboardBinding(
                AllowedBindingId,
                DashboardBindingSource.Current,
                ScopeId,
                AllowedPointId,
                RuntimePermissions.ReadPoint(AllowedPointId));
            var alarm = new DashboardBinding(
                HiddenBindingId,
                DashboardBindingSource.Alarm,
                ScopeId,
                HiddenPointId,
                RuntimePermissions.ReadPoint(HiddenPointId));
            var history = new DashboardBinding(
                HistoryBindingId,
                DashboardBindingSource.History,
                ScopeId,
                HiddenPointId,
                RuntimePermissions.ReadPoint(HiddenPointId),
                SourceId.From(Guid.Parse("d6000000-0000-7000-8000-000000000001")));
            var bindings = new[] { current, alarm, history };
            return new DashboardRevision(
                FirstDashboardId,
                DashboardRevisionId.New(),
                1,
                "Linked runtime",
                null,
                [
                    new DashboardWindow(
                        DashboardWindowId.New(),
                        "Visible",
                        [new Widget(WidgetId.New(), "runtime", "Runtime", [AllowedBindingId, HiddenBindingId])],
                        [current, alarm]),
                    new DashboardWindow(
                        DashboardWindowId.New(),
                        "Other",
                        [new Widget(WidgetId.New(), "trend", "Trend", [HistoryBindingId])],
                        [history]),
                ],
                bindings.Select(binding => new DashboardDependency(
                    binding.BindingId,
                    $"point:{binding.PointId.Value:N}:{binding.Source}",
                    $"revision:{binding.BindingId.Value:N}")).ToArray(),
                Now);
        }

        public DashboardDraftContent Draft(string name)
        {
            var revision = Revision(FirstDashboardId, name, includeHiddenBinding: false);
            return new DashboardDraftContent(
                revision.Name,
                revision.Description,
                revision.Windows,
                revision.Dependencies);
        }

        public MimicDraftContent Mimic(string svg)
        {
            var binding = new DashboardBinding(
                AllowedBindingId,
                DashboardBindingSource.Current,
                ScopeId,
                AllowedPointId,
                RuntimePermissions.ReadPoint(AllowedPointId));
            return new MimicDraftContent(
                "Pump mimic",
                svg,
                [binding],
                [new DashboardDependency(
                    binding.BindingId,
                    $"point:{binding.PointId.Value:N}",
                    $"revision:{binding.PointId.Value:N}")]);
        }

        public SessionSnapshot LinkedSession(bool includeAlarm, bool includeHistory)
        {
            var permissions = new List<PermissionCode>
            {
                DashboardPermissions.CatalogRead,
                DashboardPermissions.Read(FirstDashboardId),
                RuntimePermissions.ReadCurrent,
                RuntimePermissions.ReadPoint(AllowedPointId),
                RuntimePermissions.ReadPoint(HiddenPointId),
            };
            if (includeAlarm)
            {
                permissions.Add(EventPermissions.ReadDispatcher);
            }

            if (includeHistory)
            {
                permissions.Add(HistoryPermissions.ReadRange);
            }

            return new SessionSnapshot(
                SessionId.New(),
                SubjectId.New(),
                PrincipalKind.User,
                Now.AddMinutes(-1),
                Now.AddHours(1),
                new EffectivePermissions(permissions));
        }

        public SessionSnapshot DashboardEditorSession() => SessionWithPermissions([
            DashboardEditorPermissions.Save(FirstDashboardId),
            DashboardEditorPermissions.Validate(FirstDashboardId),
            DashboardEditorPermissions.Publish(FirstDashboardId),
            DashboardEditorPermissions.Rollback(FirstDashboardId),
        ]);

        public static SessionSnapshot MimicEditorSession(MimicId mimicId) => SessionWithPermissions([
            MimicEditorPermissions.Save(mimicId),
            MimicEditorPermissions.Validate(mimicId),
            MimicEditorPermissions.Publish(mimicId),
            MimicEditorPermissions.Rollback(mimicId),
        ]);

        private static SessionSnapshot SessionWithPermissions(IEnumerable<PermissionCode> permissions) => new(
            SessionId.New(),
            SubjectId.New(),
            PrincipalKind.User,
            Now.AddMinutes(-1),
            Now.AddHours(1),
            new EffectivePermissions(permissions));

        public SessionSnapshot Session(bool readFirst, bool readSecond)
        {
            var permissions = new List<PermissionCode>
            {
                DashboardPermissions.CatalogRead,
                DashboardPermissions.Personalize,
                RuntimePermissions.ReadCurrent,
                EventPermissions.ReadDispatcher,
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
