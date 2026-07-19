using System.Net;
using System.Net.Http.Json;
using Dispatcher.Core;
using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class RegistryProjectionTests
{
    private readonly PostgreSqlClusterFixture cluster;

    public RegistryProjectionTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task RegistryRoutesShareScopeAndDoNotLeakInaccessibleMetadata()
    {
        await using var context = await RegistryTestContext.StartAsync(cluster);
        using var http = context.CreateHttpClient();

        var scopes = await http.GetFromJsonAsync<IReadOnlyList<RegistryScopePayload>>("api/registry/scopes");
        var visibleScope = Assert.Single(scopes!);
        Assert.Equal(context.ScopeA.Value, visibleScope.ScopeId);
        Assert.Equal("Public site", visibleScope.Label);
        Assert.Equal(2, visibleScope.LocationCount);
        Assert.Equal(1, visibleScope.EquipmentCount);

        var locations = await http.GetFromJsonAsync<LocationListPayload>(
            $"api/registry/locations?scopeId={context.ScopeA.Value}");
        Assert.Equal(2, locations!.Total);
        Assert.Equal(2, locations.Items.Count);
        var hiddenLocationSearch = await http.GetFromJsonAsync<LocationListPayload>(
            $"api/registry/locations?scopeId={context.ScopeA.Value}&query=Secret");
        Assert.Equal(0, hiddenLocationSearch!.Total);
        Assert.Empty(hiddenLocationSearch.Items);

        var equipment = await http.GetFromJsonAsync<EquipmentListPayload>(
            $"api/registry/equipment?scopeId={context.ScopeA.Value}");
        Assert.Equal(1, equipment!.Total);
        Assert.Equal("AHU-1", Assert.Single(equipment.Items).Code);
        var hiddenEquipmentSearch = await http.GetFromJsonAsync<EquipmentListPayload>(
            $"api/registry/equipment?scopeId={context.ScopeA.Value}&query=Secret");
        Assert.Equal(0, hiddenEquipmentSearch!.Total);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await http.GetAsync($"api/registry/locations?scopeId={context.ScopeB.Value}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await http.GetAsync(
                $"api/registry/equipment/{context.SecretEquipmentId.Value}?scopeId={context.ScopeB.Value}")).StatusCode);

        var rawScopes = await http.GetStringAsync("api/registry/scopes");
        Assert.DoesNotContain("Secret site", rawScopes, StringComparison.Ordinal);
        Assert.DoesNotContain(context.ScopeB.Value.ToString(), rawScopes, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(context.SecretEquipmentId.Value.ToString(), rawScopes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocationPlanAndEquipmentDetailExposeOnlyAuthorizedCurrentWithHonestStatus()
    {
        await using var context = await RegistryTestContext.StartAsync(cluster);
        using var http = context.CreateHttpClient();

        var detail = await http.GetFromJsonAsync<LocationDetailPayload>(
            $"api/registry/locations/{context.RoomA.Value}?scopeId={context.ScopeA.Value}");
        Assert.Equal("Room A", detail!.Location.Name);
        Assert.Equal(context.EquipmentA.Value, Assert.Single(detail.Equipment).EquipmentId);

        var plan = await http.GetFromJsonAsync<LocationPlanContextPayload>(
            $"api/registry/locations/{context.RoomA.Value}/plan-context?scopeId={context.ScopeA.Value}");
        Assert.Equal("Public site", Assert.Single(plan!.Ancestors).Name);
        Assert.Empty(plan.Children);

        var equipment = await http.GetFromJsonAsync<EquipmentDetailPayload>(
            $"api/registry/equipment/{context.EquipmentA.Value}?scopeId={context.ScopeA.Value}");
        Assert.Equal("Stale", equipment!.CurrentStatus);
        Assert.Equal("Unknown", equipment.ConnectionStatus);
        Assert.Contains("No protocol connection evidence", equipment.ConnectionStatusReason, StringComparison.Ordinal);
        Assert.Equal(2, equipment.Points.Count);
        var allowed = equipment.Points.Single(point => point.PointId == context.AllowedPoint.Value);
        Assert.Equal(21, allowed.Current!.Value);
        Assert.Equal("Good", allowed.Current.Quality);
        Assert.Equal("Stale", allowed.Current.Freshness);
        Assert.False(allowed.Current.IsUsable);
        Assert.Null(equipment.Points.Single(point => point.PointId == context.HiddenPoint.Value).Current);

        await using var hub = context.CreateHubConnection();
        await hub.StartAsync();
        var snapshot = await hub.InvokeAsync<RuntimeSnapshotPayload>(
            "BootstrapPoints",
            context.ScopeA.Value,
            new[] { context.AllowedPoint.Value, context.HiddenPoint.Value });
        Assert.Equal(context.AllowedPoint.Value, Assert.Single(snapshot.Points).PointId);
    }

    [Theory]
    [InlineData("/locations")]
    [InlineData("/locations/62000000-0000-0000-0000-000000000001?scope=61000000-0000-0000-0000-000000000001")]
    [InlineData("/equipment")]
    [InlineData("/equipment/63000000-0000-0000-0000-000000000001")]
    public void CanonicalRegistryRoutesAreRecognized(string route) => Assert.True(RegistryRoutes.IsCanonical(route));

    [Theory]
    [InlineData("/locations/not-a-guid")]
    [InlineData("/equipment/63000000-0000-0000-0000-000000000001/edit")]
    [InlineData("/history")]
    public void NonRegistryRoutesAreRejected(string route) => Assert.False(RegistryRoutes.IsCanonical(route));

    private sealed class RegistryTestContext : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly TestDatabase database;
        private readonly SessionSnapshot session;

        private RegistryTestContext(
            WebApplication app,
            Uri address,
            TestDatabase database,
            SessionSnapshot session,
            FacilityScopeId scopeA,
            FacilityScopeId scopeB,
            LocationId roomA,
            EquipmentId equipmentA,
            EquipmentId secretEquipmentId,
            PointId allowedPoint,
            PointId hiddenPoint)
        {
            this.app = app;
            Address = address;
            this.database = database;
            this.session = session;
            ScopeA = scopeA;
            ScopeB = scopeB;
            RoomA = roomA;
            EquipmentA = equipmentA;
            SecretEquipmentId = secretEquipmentId;
            AllowedPoint = allowedPoint;
            HiddenPoint = hiddenPoint;
        }

        public Uri Address { get; }
        public FacilityScopeId ScopeA { get; }
        public FacilityScopeId ScopeB { get; }
        public LocationId RoomA { get; }
        public EquipmentId EquipmentA { get; }
        public EquipmentId SecretEquipmentId { get; }
        public PointId AllowedPoint { get; }
        public PointId HiddenPoint { get; }

        public static async Task<RegistryTestContext> StartAsync(PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            await using (var dataSource = NpgsqlDataSource.Create(database.ConnectionString))
            {
                await PostgresMigrationRunner.ApplyAsync(
                    dataSource,
                    FacilityMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
                await PostgresMigrationRunner.ApplyAsync(
                    dataSource,
                    EquipmentMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));

                var scopeA = FacilityScopeId.From(Guid.Parse("61000000-0000-0000-0000-000000000011"));
                var scopeB = FacilityScopeId.From(Guid.Parse("61000000-0000-0000-0000-000000000012"));
                var rootA = LocationId.From(Guid.Parse("62000000-0000-0000-0000-000000000011"));
                var roomA = LocationId.From(Guid.Parse("62000000-0000-0000-0000-000000000012"));
                var rootB = LocationId.From(Guid.Parse("62000000-0000-0000-0000-000000000013"));
                var equipmentA = EquipmentId.From(Guid.Parse("63000000-0000-0000-0000-000000000011"));
                var equipmentB = EquipmentId.From(Guid.Parse("63000000-0000-0000-0000-000000000012"));
                var allowedPoint = PointId.From(Guid.Parse("64000000-0000-0000-0000-000000000011"));
                var hiddenPoint = PointId.From(Guid.Parse("64000000-0000-0000-0000-000000000012"));
                var now = DateTimeOffset.UtcNow;
                var seedSession = new SessionSnapshot(
                    SessionId.New(),
                    SubjectId.New(),
                    PrincipalKind.User,
                    now.AddMinutes(-1),
                    now.AddHours(1),
                    new EffectivePermissions([
                        FacilityPermissions.Read(scopeA), FacilityPermissions.Write(scopeA),
                        FacilityPermissions.Read(scopeB), FacilityPermissions.Write(scopeB),
                        EquipmentPermissions.Read(scopeA), EquipmentPermissions.Write(scopeA),
                        EquipmentPermissions.Read(scopeB), EquipmentPermissions.Write(scopeB),
                    ]));
                var facilityStore = new FacilityStore(
                    dataSource,
                    PostgreSqlClusterFixture.OwnerARole,
                    SystemClock.Instance);
                var facilities = new FacilityService(facilityStore, SystemClock.Instance);
                var equipment = new EquipmentService(
                    new EquipmentStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, SystemClock.Instance),
                    facilityStore,
                    SystemClock.Instance);
                Assert.True((await facilities.CreateLocationAsync(
                    seedSession,
                    new CreateLocation(rootA, scopeA, null, "PUBLIC", "Public site"))).IsSuccess);
                Assert.True((await facilities.CreateLocationAsync(
                    seedSession,
                    new CreateLocation(roomA, scopeA, rootA, "ROOM-A", "Room A"))).IsSuccess);
                Assert.True((await facilities.CreateLocationAsync(
                    seedSession,
                    new CreateLocation(rootB, scopeB, null, "SECRET", "Secret site"))).IsSuccess);
                Assert.True((await equipment.CreateEquipmentAsync(
                    seedSession,
                    new CreateEquipment(equipmentA, scopeA, roomA, "AHU-1", "Air handler"))).IsSuccess);
                Assert.True((await equipment.AddPointAsync(
                    seedSession,
                    scopeA,
                    new AddPointDefinition(equipmentA, allowedPoint, "SUPPLY", "Supply", Unit.FromSymbol("C"), 1))).IsSuccess);
                Assert.True((await equipment.AddPointAsync(
                    seedSession,
                    scopeA,
                    new AddPointDefinition(equipmentA, hiddenPoint, "RETURN", "Return", Unit.FromSymbol("C"), 2))).IsSuccess);
                Assert.True((await equipment.CreateEquipmentAsync(
                    seedSession,
                    new CreateEquipment(equipmentB, scopeB, rootB, "SECRET-AHU", "Secret equipment"))).IsSuccess);

                var session = new SessionSnapshot(
                    SessionId.New(),
                    SubjectId.New(),
                    PrincipalKind.User,
                    now.AddMinutes(-1),
                    now.AddHours(1),
                    new EffectivePermissions([
                        FacilityPermissions.Read(scopeA),
                        EquipmentPermissions.Read(scopeA),
                        RuntimePermissions.ReadCurrent,
                        RuntimePermissions.ReadPoint(allowedPoint),
                    ]));

                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
                builder.Services.AddDispatcherServer(builder.Configuration);
                builder.Services.AddRegistryServer(
                    database.ConnectionString,
                    PostgreSqlClusterFixture.OwnerARole,
                    PostgreSqlClusterFixture.OwnerBRole);
                builder.Services.Configure<TestSessionBridgeOptions>(options => options.Enabled = true);
                var app = builder.Build();
                app.MapDispatcherServer();
                app.MapRegistryServer();
                app.Services.GetRequiredService<SessionDirectory>().Set(session);

                var runtimeScope = RuntimeScopeId.From(scopeA.Value);
                var runtime = new CoreRuntime(runtimeScope, SystemClock.Instance, SystemClock.Instance);
                app.Services.GetRequiredService<RuntimeRegistry>().Add(runtimeScope, runtime);
                var allowedBinding = new SourceBinding(
                    runtimeScope,
                    SourceId.From(Guid.Parse("65000000-0000-0000-0000-000000000011")),
                    SourceBindingGeneration.From(1),
                    SourceSessionGeneration.From(1));
                var hiddenBinding = new SourceBinding(
                    runtimeScope,
                    SourceId.From(Guid.Parse("65000000-0000-0000-0000-000000000012")),
                    SourceBindingGeneration.From(1),
                    SourceSessionGeneration.From(1));
                Assert.True(runtime.ActivateBinding(allowedBinding).IsSuccess);
                Assert.True(runtime.ActivateBinding(hiddenBinding).IsSuccess);
                var allowedCut = RuntimeCut.Normalize(allowedBinding, 1, [new SourceObservation(
                    runtimeScope,
                    allowedBinding.SourceId,
                    allowedPoint,
                    new OwnerPosition<SourceObservation>(1),
                    TypedValue.From(21L),
                    Unit.FromSymbol("C"),
                    DataQuality.Good,
                    Freshness.Stale,
                    SourceTimestamp.FromUtc(now))]);
                var hiddenCut = RuntimeCut.Normalize(hiddenBinding, 1, [new SourceObservation(
                    runtimeScope,
                    hiddenBinding.SourceId,
                    hiddenPoint,
                    new OwnerPosition<SourceObservation>(1),
                    TypedValue.From(99L),
                    Unit.FromSymbol("C"),
                    DataQuality.Good,
                    Freshness.Fresh,
                    SourceTimestamp.FromUtc(now))]);
                Assert.True(allowedCut.IsSuccess);
                Assert.True(hiddenCut.IsSuccess);
                Assert.True(runtime.Apply(allowedCut.Value).IsSuccess);
                Assert.True(runtime.Apply(hiddenCut.Value).IsSuccess);

                await app.StartAsync();
                var address = new Uri(Assert.Single(app.Services
                    .GetRequiredService<IServer>()
                    .Features
                    .Get<IServerAddressesFeature>()!
                    .Addresses));
                return new RegistryTestContext(
                    app,
                    address,
                    database,
                    session,
                    scopeA,
                    scopeB,
                    roomA,
                    equipmentA,
                    equipmentB,
                    allowedPoint,
                    hiddenPoint);
            }
        }

        public HttpClient CreateHttpClient()
        {
            var client = new HttpClient { BaseAddress = Address };
            client.DefaultRequestHeaders.Add(RequestSessionResolver.HeaderName, session.Id.Value.ToString());
            return client;
        }

        public HubConnection CreateHubConnection() => new HubConnectionBuilder()
            .WithUrl(new Uri(Address, "hubs/runtime"), options =>
                options.Headers.Add(RequestSessionResolver.HeaderName, session.Id.Value.ToString()))
            .Build();

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            await database.DisposeAsync();
        }
    }
}
