using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Dispatcher.Core;
using Dispatcher.Identity;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ServerSessionPayload = Dispatcher.Server.ProductionSessionPayload;
using ServerBootstrapPayload = Dispatcher.Server.SessionBootstrapPayload;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class ServerProductionSessionTests
{
    private readonly PostgreSqlClusterFixture cluster;

    public ServerProductionSessionTests(PostgreSqlClusterFixture cluster) => this.cluster = cluster;

    [Fact]
    public async Task SameOriginProductionSessionRefreshRevokePermissionChangeAndLongPollingAreEnforced()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = Npgsql.NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            IdentityMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));

        var webRoot = FindProjectPath("src", "Dispatcher.Web", "wwwroot");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            WebRootPath = webRoot,
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dispatcher:TestSessionBridge:Enabled"] = "false",
        });
        builder.Services.AddDispatcherServer(builder.Configuration);
        builder.Services.AddIdentityServer(
            database.ConnectionString,
            PostgreSqlClusterFixture.OwnerBRole,
            new IdentitySecurityPolicy(
                10_000, 12, 128, 3, TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(10), TimeSpan.FromHours(1)));

        await using var app = builder.Build();
        var observed = new ConcurrentQueue<(string Url, string Authorization)>();
        app.Use(async (context, next) =>
        {
            observed.Enqueue((
                context.Request.Path + context.Request.QueryString,
                context.Request.Headers.Authorization.ToString()));
            context.Response.OnStarting(() =>
            {
                if (context.Response.ContentType?.StartsWith(
                        "text/html", StringComparison.OrdinalIgnoreCase) == true)
                {
                    context.Response.Headers.CacheControl = "no-store, no-cache";
                    context.Response.Headers.Pragma = "no-cache";
                }
                return Task.CompletedTask;
            });
            await next(context);
        });
        app.UseStaticFiles();
        app.UseProductionSessionAuthentication();
        app.MapDispatcherServer();
        app.MapIdentityServer();
        app.MapFallbackToFile("index.html");

        var identities = app.Services.GetRequiredService<IdentityStore>();
        var administratorId = IdentityAccountId.New();
        var administratorRoleId = IdentityRoleId.New();
        const string administratorPassword = "Correct-Horse-35!";
        Assert.True((await identities.BootstrapAdministratorAsync(new(
            administratorId,
            SubjectId.New(),
            null,
            administratorRoleId,
            "dispatcher-admin",
            administratorPassword))).IsSuccess);
        var administratorLogin = (await identities.LoginAsync(new(
            "dispatcher-admin", administratorPassword))).Value;
        var administratorMutation = SessionAuthorization.AuthorizeMutation(
            administratorLogin.Session,
            IdentityPermissions.Administer,
            SystemClock.Instance).Value;
        var administratorAccess = SessionAuthorization.AuthorizeAccess(
            administratorLogin.Session,
            IdentityPermissions.Administer,
            SystemClock.Instance).Value;
        var scopeId = IdentityScopeId.New();
        var roleId = IdentityRoleId.New();
        var accountId = IdentityAccountId.New();
        var runtimeRead = RuntimePermissions.ReadCurrent;
        Assert.True((await identities.CreateScopeAsync(
            administratorMutation, new(scopeId, "Operations"))).IsSuccess);
        Assert.True((await identities.CreateRoleAsync(
            administratorMutation,
            new(roleId, "Operators", [new(runtimeRead, scopeId)]))).IsSuccess);
        Assert.True((await identities.CreateAccountAsync(
            administratorMutation,
            new(accountId, SubjectId.New(), null, scopeId,
                "operator", "Operator-Secret-35!"))).IsSuccess);
        Assert.True((await identities.AssignRoleToAccountAsync(
            administratorMutation, accountId, roleId)).IsSuccess);

        await app.StartAsync();
        var address = new Uri(Assert.Single(app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses));
        using var http = new HttpClient { BaseAddress = address };

        var root = await http.GetAsync("/");
        var directRoute = await http.GetAsync("/current");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Equal(HttpStatusCode.OK, directRoute.StatusCode);
        Assert.Contains("no-store", directRoute.Headers.CacheControl!.ToString());
        Assert.Contains("<div id=\"app\">", await directRoute.Content.ReadAsStringAsync());
        Assert.False(directRoute.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await http.GetAsync($"api/runtime/{scopeId.Value}/snapshot")).StatusCode);

        var loginResponse = await http.PostAsJsonAsync(
            "api/auth/login",
            new { UserName = "operator", Password = "Operator-Secret-35!" });
        var session = (await loginResponse.Content.ReadFromJsonAsync<ServerSessionPayload>())!;
        SetSessionHeader(http, session.AccessToken);
        var bootstrap = await http.GetFromJsonAsync<ServerBootstrapPayload>("api/auth/bootstrap");
        Assert.Equal(scopeId.Value, bootstrap!.DefaultScopeId);
        Assert.Equal([scopeId.Value], bootstrap.AllowedScopeIds);

        await using (var hub = CreateRuntimeHub(address, session.AccessToken))
        {
            await hub.StartAsync();
        }
        Assert.Contains(observed, request =>
            request.Url.StartsWith("/hubs/runtime", StringComparison.Ordinal) &&
            request.Authorization == $"Dispatcher-Session {session.AccessToken}");
        Assert.DoesNotContain(observed, request =>
            request.Url.Contains(session.AccessToken, StringComparison.Ordinal) ||
            request.Url.Contains(session.RefreshToken, StringComparison.Ordinal));

        var refreshResponse = await http.PostAsJsonAsync(
            "api/auth/refresh",
            new { session.RefreshToken });
        var refreshed = (await refreshResponse.Content.ReadFromJsonAsync<ServerSessionPayload>())!;
        SetSessionHeader(http, session.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("api/auth/bootstrap")).StatusCode);
        SetSessionHeader(http, refreshed.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/auth/bootstrap")).StatusCode);

        var changedGrants = new[]
        {
            new IdentityPermissionGrant(
                PermissionCode.From("workspace.home.read"),
                scopeId),
        };
        var preview = await identities.PreviewRolePermissionsAsync(
            administratorAccess, roleId, changedGrants);
        Assert.True((await identities.UpdateRolePermissionsAsync(
            administratorMutation,
            roleId,
            changedGrants,
            StateVersion.Initial,
            preview.Value.Fingerprint)).IsSuccess);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("api/auth/bootstrap")).StatusCode);

        var replacementResponse = await http.PostAsJsonAsync(
            "api/auth/login",
            new { UserName = "operator", Password = "Operator-Secret-35!" });
        var replacement = (await replacementResponse.Content.ReadFromJsonAsync<ServerSessionPayload>())!;
        SetSessionHeader(http, replacement.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync("api/auth/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("api/auth/bootstrap")).StatusCode);

        await app.StopAsync();
    }

    private static HubConnection CreateRuntimeHub(Uri address, string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(address, "hubs/runtime"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.Headers["Authorization"] = $"Dispatcher-Session {accessToken}";
            })
            .Build();

    private static void SetSessionHeader(HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization",
            $"Dispatcher-Session {accessToken}");
    }

    private static string FindProjectPath(params string[] path)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Dispatcher.slnx")))
                return Path.Combine([directory.FullName, .. path]);
        }
        throw new DirectoryNotFoundException("Dispatcher project root was not found.");
    }
}
