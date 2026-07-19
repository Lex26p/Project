using System.Net;
using System.Net.Http.Json;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Server;
using Dispatcher.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class WorkspaceTests
{
    private readonly PostgreSqlClusterFixture cluster;

    public WorkspaceTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task AssignedHomePersonalOverridesAndPreferencesSurviveRestart()
    {
        await using var context = await WorkspaceTestContext.CreateAsync(cluster);

        var home = await context.Service.ReadHomeAsync(context.Session);
        Assert.True(home.IsSuccess);
        Assert.Equal(
            ["Personal link", "Role operations", "Organization overview"],
            home.Value.Items.Select(item => item.Label));
        Assert.DoesNotContain(home.Value.Items, item => item.Label == "Secret module");
        Assert.DoesNotContain(home.Value.Items, item => item.Label == "Other role");

        Assert.True((await context.Service.SetHomeOverrideAsync(
            context.Session,
            HomeItemId.From(WorkspaceTestContext.RoleItemId),
            true,
            null)).IsSuccess);
        Assert.DoesNotContain(
            (await context.Service.ReadHomeAsync(context.Session)).Value.Items,
            item => item.Label == "Role operations");
        Assert.True((await context.Service.SetHomeOverrideAsync(
            context.Session,
            HomeItemId.From(WorkspaceTestContext.RoleItemId),
            false,
            1)).IsSuccess);

        Assert.True((await context.Service.UpdatePreferencesAsync(
            context.Session,
            new WorkspacePreferences(true, "/me"))).IsSuccess);
        Assert.True((await context.Service.SetFavoriteAsync(
            context.Session,
            HomeItemId.From(WorkspaceTestContext.RoleItemId),
            true)).IsSuccess);
        Assert.True((await context.Service.RecordRecentAsync(
            context.Session,
            HomeItemId.From(WorkspaceTestContext.RoleItemId))).IsSuccess);

        var restarted = new WorkspaceService(
            new WorkspaceStore(context.DataSource, PostgreSqlClusterFixture.OwnerARole, SystemClock.Instance),
            SystemClock.Instance);
        var restoredHome = await restarted.ReadHomeAsync(context.Session);
        var favorites = await restarted.ReadFavoritesAsync(context.Session);
        var recent = await restarted.ReadRecentAsync(context.Session);

        Assert.True(restoredHome.Value.Preferences.CompactNavigation);
        Assert.Equal("/me", restoredHome.Value.Preferences.StartRoute);
        Assert.Equal("Role operations", Assert.Single(favorites.Value).Label);
        Assert.Equal("Role operations", Assert.Single(recent.Value).Label);

        await using var audit = context.DataSource.CreateCommand(
            $"SELECT count(*) FROM {WorkspaceMigrations.Schema}.preference_audit;");
        Assert.Equal(5L, await audit.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ViewerFilteringRemovesPrivateProfilesHiddenLabelsAndCounts()
    {
        await using var context = await WorkspaceTestContext.CreateAsync(cluster);

        var visible = await context.Service.ReadUserAsync(
            context.Session,
            AccountId.From(WorkspaceTestContext.VisibleAccountId));
        var privateProfile = await context.Service.ReadUserAsync(
            context.Session,
            AccountId.From(WorkspaceTestContext.PrivateAccountId));
        var visibleSearch = await context.Service.SearchAsync(context.Session, "Visible");
        var privateSearch = await context.Service.SearchAsync(context.Session, "Private");
        var hiddenItemSearch = await context.Service.SearchAsync(context.Session, "Secret");
        var grantsBefore = context.Session.Permissions.Grants.ToHashSet();
        var profileUpdate = await context.Service.UpdateProfileAsync(
            context.Session,
            new ProfileSettings("Viewer Renamed", "Operator", PersonAvailability.Away, ProfileVisibility.Private));

        Assert.True(visible.IsSuccess);
        Assert.Equal("Visible Person", visible.Value.DisplayName);
        Assert.Equal("workspace.profile_not_found", privateProfile.Error?.Code.Value);
        Assert.Equal("Visible Person", Assert.Single(visibleSearch.Value).Label);
        Assert.Empty(privateSearch.Value);
        Assert.Empty(hiddenItemSearch.Value);
        Assert.True(profileUpdate.IsSuccess);
        Assert.True(grantsBefore.SetEquals(context.Session.Permissions.Grants));
    }

    [Fact]
    public async Task DirectRoutesReauthorizeAndExpiredSessionFailsClosed()
    {
        await using var context = await WorkspaceTestContext.CreateAsync(cluster);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddDispatcherServer(builder.Configuration);
        builder.Services.AddWorkspaceServer(context.Database.ConnectionString, PostgreSqlClusterFixture.OwnerARole);
        builder.Services.Configure<TestSessionBridgeOptions>(options => options.Enabled = true);
        await using var app = builder.Build();
        app.MapDispatcherServer();
        app.MapWorkspaceServer();
        app.Services.GetRequiredService<SessionDirectory>().Set(context.Session);
        await app.StartAsync();

        var address = new Uri(Assert.Single(app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses));
        using var http = new HttpClient { BaseAddress = address };
        http.DefaultRequestHeaders.Add(RequestSessionResolver.HeaderName, context.Session.Id.Value.ToString());

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/workspace/access?route=%2Fhome")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(
            $"api/workspace/users/{WorkspaceTestContext.VisibleAccountId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(
            $"api/workspace/users/{WorkspaceTestContext.PrivateAccountId}")).StatusCode);

        var home = await http.GetFromJsonAsync<WorkspaceHomePayload>("api/workspace/home");
        Assert.Equal(3, home!.Items.Count);
        Assert.DoesNotContain(home.Items, item => item.Label == "Secret module");

        var now = DateTimeOffset.UtcNow;
        app.Services.GetRequiredService<SessionDirectory>().Set(new SessionSnapshot(
            context.Session.Id,
            context.Session.SubjectId,
            context.Session.PrincipalKind,
            now.AddHours(-2),
            now.AddHours(-1),
            context.Session.Permissions));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await http.GetAsync("api/workspace/access?route=%2Fhome")).StatusCode);

        await app.StopAsync();
    }

    private sealed class WorkspaceTestContext : IAsyncDisposable
    {
        public static readonly Guid ViewerAccountId = Guid.Parse("51000000-0000-0000-0000-000000000001");
        public static readonly Guid VisibleAccountId = Guid.Parse("51000000-0000-0000-0000-000000000002");
        public static readonly Guid PrivateAccountId = Guid.Parse("51000000-0000-0000-0000-000000000003");
        public static readonly Guid RoleItemId = Guid.Parse("53000000-0000-0000-0000-000000000002");
        private static readonly Guid ViewerPersonId = Guid.Parse("52000000-0000-0000-0000-000000000001");
        private static readonly Guid VisiblePersonId = Guid.Parse("52000000-0000-0000-0000-000000000002");
        private static readonly Guid PrivatePersonId = Guid.Parse("52000000-0000-0000-0000-000000000003");
        private static readonly Guid PersonalItemId = Guid.Parse("53000000-0000-0000-0000-000000000001");
        private static readonly Guid OrganizationItemId = Guid.Parse("53000000-0000-0000-0000-000000000003");
        private static readonly Guid SecretItemId = Guid.Parse("53000000-0000-0000-0000-000000000004");
        private static readonly Guid OtherRoleItemId = Guid.Parse("53000000-0000-0000-0000-000000000005");

        private WorkspaceTestContext(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            WorkspaceService service,
            SessionSnapshot session)
        {
            Database = database;
            DataSource = dataSource;
            Service = service;
            Session = session;
        }

        public TestDatabase Database { get; }

        public NpgsqlDataSource DataSource { get; }

        public WorkspaceService Service { get; }

        public SessionSnapshot Session { get; }

        public static async Task<WorkspaceTestContext> CreateAsync(PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                WorkspaceMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));

            var subjectId = SubjectId.New();
            await SeedAsync(dataSource, subjectId);
            var permissions = new EffectivePermissions([
                WorkspacePermissions.HomeRead,
                WorkspacePermissions.HomeCustomize,
                WorkspacePermissions.ProfileSelfRead,
                WorkspacePermissions.ProfileSelfWrite,
                WorkspacePermissions.ProfileUserRead,
                WorkspacePermissions.Search,
                WorkspacePermissions.FavoritesWrite,
                WorkspacePermissions.RecentsWrite,
                WorkspacePermissions.PreferencesWrite,
                PermissionCode.From("allowed.item.read"),
            ]);
            var now = DateTimeOffset.UtcNow;
            var session = new SessionSnapshot(
                SessionId.New(),
                subjectId,
                PrincipalKind.User,
                now.AddMinutes(-1),
                now.AddHours(1),
                permissions);
            var service = new WorkspaceService(
                new WorkspaceStore(dataSource, PostgreSqlClusterFixture.OwnerARole, SystemClock.Instance),
                SystemClock.Instance);
            return new WorkspaceTestContext(database, dataSource, service, session);
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }

        private static async Task SeedAsync(NpgsqlDataSource dataSource, SubjectId viewerSubjectId)
        {
            await using var command = dataSource.CreateCommand(
                $"""
                INSERT INTO {WorkspaceMigrations.Schema}.person
                    (person_id, display_name, title, availability, visibility)
                VALUES
                    ('{ViewerPersonId}', 'Viewer Person', 'Operator', 1, 1),
                    ('{VisiblePersonId}', 'Visible Person', 'Engineer', 2, 1),
                    ('{PrivatePersonId}', 'Private Person', 'Manager', 3, 2);
                INSERT INTO {WorkspaceMigrations.Schema}.account
                    (account_id, subject_id, person_id)
                VALUES
                    ('{ViewerAccountId}', '{viewerSubjectId.Value}', '{ViewerPersonId}'),
                    ('{VisibleAccountId}', '54000000-0000-0000-0000-000000000002', '{VisiblePersonId}'),
                    ('{PrivateAccountId}', '54000000-0000-0000-0000-000000000003', '{PrivatePersonId}');
                INSERT INTO {WorkspaceMigrations.Schema}.account_membership
                    (account_id, audience_kind, audience_key)
                VALUES
                    ('{ViewerAccountId}', 2, 'operators'),
                    ('{ViewerAccountId}', 3, 'north'),
                    ('{VisibleAccountId}', 3, 'north'),
                    ('{PrivateAccountId}', 3, 'north');
                INSERT INTO {WorkspaceMigrations.Schema}.home_item
                    (item_id, label, route, required_permission, audience_kind, audience_key, default_order)
                VALUES
                    ('{PersonalItemId}', 'Personal link', '/me', 'allowed.item.read', 1, '{ViewerAccountId:N}', 10),
                    ('{RoleItemId}', 'Role operations', '/current', 'allowed.item.read', 2, 'operators', 20),
                    ('{OrganizationItemId}', 'Organization overview', '/home', 'allowed.item.read', 3, 'north', 30),
                    ('{SecretItemId}', 'Secret module', '/secret', 'hidden.item.read', 2, 'operators', 40),
                    ('{OtherRoleItemId}', 'Other role', '/other', 'allowed.item.read', 2, 'managers', 50);
                """);
            await command.ExecuteNonQueryAsync();
        }
    }
}
