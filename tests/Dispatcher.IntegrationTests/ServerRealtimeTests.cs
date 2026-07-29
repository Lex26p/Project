using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Dispatcher.Core;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Server;
using Dispatcher.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using WebPollKind = Dispatcher.Web.RealtimePollKind;
using WebPollPayload = Dispatcher.Web.RealtimePollPayload;
using WebSnapshotPayload = Dispatcher.Web.RuntimeSnapshotPayload;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class ServerRealtimeTests
{
    private static readonly DateTimeOffset ObservationTime =
        new(2026, 7, 19, 3, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public ServerRealtimeTests(PostgreSqlClusterFixture cluster) => this.cluster = cluster;

    [Fact]
    public async Task AuthorizedHttpAndRealtimeExposeOnlyAllowedPointAndCatchUpSlowConsumer()
    {
        await using var host = await RealtimeHost.StartAsync(cluster);
        using var http = host.CreateHttpClient();

        var readinessResponse = await http.GetAsync($"api/runtime/{host.ScopeId.Value}/readiness");
        Assert.Equal(HttpStatusCode.OK, readinessResponse.StatusCode);
        var readiness = await readinessResponse.Content.ReadFromJsonAsync<RuntimeReadinessPayload>();
        Assert.True(readiness!.CanServeCurrent);

        var response = await http.GetAsync($"api/runtime/{host.ScopeId.Value}/snapshot");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var httpSnapshot = await response.Content.ReadFromJsonAsync<WebSnapshotPayload>();
        Assert.Equal(host.AllowedPoint.Value, Assert.Single(httpSnapshot!.Points).PointId);

        await using var hub = host.CreateHubConnection();
        await hub.StartAsync();
        var snapshot = await hub.InvokeAsync<WebSnapshotPayload>("Bootstrap", host.ScopeId.Value);
        var state = new RealtimeWidgetState();
        state.ApplySnapshot(snapshot);
        Assert.True(state.ConsumeRenderRequest());

        await host.AdmitHiddenAsync(99, 2);
        var hiddenOnly = await hub.InvokeAsync<WebPollPayload>(
            "Poll", host.ScopeId.Value, state.Cursor);
        state.ApplyPoll(hiddenOnly);
        Assert.Equal(WebPollKind.NoChange, hiddenOnly.Kind);
        Assert.False(state.ConsumeRenderRequest());

        await host.AdmitAllowedAsync(11, 2);
        await host.AdmitAllowedAsync(12, 3);
        await host.AdmitAllowedAsync(13, 4);
        await host.AdmitHiddenAsync(100, 3);

        var poll = await hub.InvokeAsync<WebPollPayload>("Poll", host.ScopeId.Value, state.Cursor);
        state.ApplyPoll(poll);

        Assert.Equal(WebPollKind.Delta, poll.Kind);
        Assert.All(poll.Delta!.Changes, point => Assert.Equal(host.AllowedPoint.Value, point.PointId));
        Assert.Equal(13, Assert.Single(state.Points).Value);
        Assert.True(state.ConsumeRenderRequest());
        Assert.False(state.ConsumeRenderRequest());
    }

    [Fact]
    public async Task GapDisconnectAndPermissionChangeForceBootstrapOrReauthorization()
    {
        await using var host = await RealtimeHost.StartAsync(cluster);
        await using var hub = host.CreateHubConnection();
        await hub.StartAsync();
        var snapshot = await hub.InvokeAsync<WebSnapshotPayload>("Bootstrap", host.ScopeId.Value);

        var gap = await hub.InvokeAsync<WebPollPayload>("Poll", host.ScopeId.Value, snapshot.Cursor + 1);
        Assert.Equal(WebPollKind.Gap, gap.Kind);
        var resnapshot = await hub.InvokeAsync<WebSnapshotPayload>("Bootstrap", host.ScopeId.Value);
        Assert.Equal(host.AllowedPoint.Value, Assert.Single(resnapshot.Points).PointId);

        await host.AdmitAllowedAsync(11, 2, retainedDeltaCapacity: 2);
        await host.AdmitAllowedAsync(12, 3, retainedDeltaCapacity: 2);
        await host.AdmitAllowedAsync(13, 4, retainedDeltaCapacity: 2);
        var databaseGap = await hub.InvokeAsync<WebPollPayload>(
            "Poll", host.ScopeId.Value, resnapshot.Cursor);
        Assert.Equal(WebPollKind.Gap, databaseGap.Kind);
        await hub.InvokeAsync<WebSnapshotPayload>("Bootstrap", host.ScopeId.Value);

        await hub.StopAsync();
        var reconnectTransport = host.CreateHubConnection();
        await using var client = new RealtimeWidgetClient(reconnectTransport);
        await client.StartAsync(host.ScopeId.Value, CancellationToken.None);
        await reconnectTransport.StopAsync();
        await client.PollAsync(CancellationToken.None);
        Assert.False(client.State.NeedsResync);
        Assert.Equal(host.AllowedPoint.Value, Assert.Single(client.State.Points).PointId);

        host.RemovePointPermission();
        await client.PollAsync(CancellationToken.None);
        Assert.True(client.State.PermissionInvalidated);
        Assert.Empty(client.State.Points);
    }

    [Fact]
    public async Task MissingPublishedCurrentConfigurationFailsClosed()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Test",
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddDispatcherServer(builder.Configuration);
        builder.Services.Configure<TestSessionBridgeOptions>(options => options.Enabled = true);
        await using var app = builder.Build();
        app.MapDispatcherServer();
        var session = CreateSession(PointId.From(Guid.NewGuid()));
        app.Services.GetRequiredService<SessionDirectory>().Set(session);
        await app.StartAsync();
        var address = new Uri(Assert.Single(app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses));
        using var http = new HttpClient { BaseAddress = address };
        http.DefaultRequestHeaders.Add(
            RequestSessionResolver.HeaderName,
            session.Id.Value.ToString());

        var response = await http.GetAsync($"api/runtime/{Guid.NewGuid()}/snapshot");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await app.StopAsync();
    }

    private sealed class RealtimeHost : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly TestDatabase database;
        private readonly NpgsqlDataSource writerDataSource;
        private readonly SessionSnapshot session;
        private readonly SourceId allowedSource = SourceId.From(
            Guid.Parse("30000000-0000-0000-0000-000000000001"));
        private readonly SourceId hiddenSource = SourceId.From(
            Guid.Parse("30000000-0000-0000-0000-000000000002"));
        private readonly CoreRuntime runtime;
        private readonly CoreRuntimeStore store;

        private RealtimeHost(
            WebApplication app,
            TestDatabase database,
            NpgsqlDataSource writerDataSource,
            Uri address,
            SessionSnapshot session,
            RuntimeScopeId scopeId,
            PointId allowedPoint,
            PointId hiddenPoint,
            CoreRuntime runtime,
            CoreRuntimeStore store)
        {
            this.app = app;
            this.database = database;
            this.writerDataSource = writerDataSource;
            Address = address;
            this.session = session;
            ScopeId = scopeId;
            AllowedPoint = allowedPoint;
            HiddenPoint = hiddenPoint;
            this.runtime = runtime;
            this.store = store;
        }

        public Uri Address { get; }

        public RuntimeScopeId ScopeId { get; }

        public PointId AllowedPoint { get; }

        public PointId HiddenPoint { get; }

        public static async Task<RealtimeHost> StartAsync(PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var writerDataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMigrationRunner.ApplyAsync(
                writerDataSource,
                CoreRuntimeMigrations.CreatePlan(
                    PostgreSqlClusterFixture.OwnerBRole,
                    PostgreSqlClusterFixture.OwnerARole));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Test",
            });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Dispatcher"] = database.ConnectionString,
                ["Dispatcher:Core:PublishedReadRole"] = PostgreSqlClusterFixture.OwnerARole,
                ["Dispatcher:Core:MaxSnapshotPoints"] = "128",
                ["Dispatcher:Core:MaxDeltaChanges"] = "128",
            });
            builder.Services.AddDispatcherServer(builder.Configuration);
            builder.Services.Configure<TestSessionBridgeOptions>(options => options.Enabled = true);
            var app = builder.Build();
            app.MapDispatcherServer();

            var scopeId = RuntimeScopeId.From(Guid.Parse("10000000-0000-0000-0000-000000000001"));
            var allowedPoint = PointId.From(Guid.Parse("20000000-0000-0000-0000-000000000001"));
            var hiddenPoint = PointId.From(Guid.Parse("20000000-0000-0000-0000-000000000002"));
            var runtime = new CoreRuntime(
                scopeId,
                SystemClock.Instance,
                SystemClock.Instance,
                new RuntimeCurrentLimits(maxPoints: 128, retainedChangeCapacity: 1024));
            var allowedBinding = new SourceBinding(
                scopeId,
                SourceId.From(Guid.Parse("30000000-0000-0000-0000-000000000001")),
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1));
            var hiddenBinding = new SourceBinding(
                scopeId,
                SourceId.From(Guid.Parse("30000000-0000-0000-0000-000000000002")),
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1));
            Assert.True(runtime.ActivateBinding(allowedBinding).IsSuccess);
            Assert.True(runtime.ActivateBinding(hiddenBinding).IsSuccess);
            var store = new CoreRuntimeStore(
                writerDataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                SystemClock.Instance);

            var session = CreateSession(allowedPoint);
            app.Services.GetRequiredService<SessionDirectory>().Set(session);

            await app.StartAsync();
            var addresses = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!;
            var address = new Uri(Assert.Single(addresses.Addresses));
            var host = new RealtimeHost(
                app,
                database,
                writerDataSource,
                address,
                session,
                scopeId,
                allowedPoint,
                hiddenPoint,
                runtime,
                store);
            await host.AdmitAllowedAsync(10, 1);
            await host.AdmitHiddenAsync(90, 1);
            return host;
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

        public Task AdmitAllowedAsync(
            long value,
            ulong sourcePosition,
            int retainedDeltaCapacity = 32) =>
            AdmitAsync(
                allowedSource,
                AllowedPoint,
                value,
                sourcePosition,
                retainedDeltaCapacity);

        public Task AdmitHiddenAsync(
            long value,
            ulong sourcePosition,
            int retainedDeltaCapacity = 32) =>
            AdmitAsync(
                hiddenSource,
                HiddenPoint,
                value,
                sourcePosition,
                retainedDeltaCapacity);

        public void RemovePointPermission() =>
            app.Services.GetRequiredService<SessionDirectory>().Set(new SessionSnapshot(
                session.Id,
                session.SubjectId,
                session.PrincipalKind,
                session.IssuedAt,
                session.ExpiresAt,
                new EffectivePermissions([RuntimePermissions.ReadCurrent])));

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            await writerDataSource.DisposeAsync();
            await database.DisposeAsync();
        }

        private async Task AdmitAsync(
            SourceId sourceId,
            PointId pointId,
            long value,
            ulong sourcePosition,
            int retainedDeltaCapacity)
        {
            var binding = new SourceBinding(
                ScopeId,
                sourceId,
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1));
            var observation = new SourceObservation(
                ScopeId,
                sourceId,
                pointId,
                new OwnerPosition<SourceObservation>(sourcePosition),
                TypedValue.From((decimal)value),
                Unit.FromSymbol("°C"),
                DataQuality.Good,
                Freshness.Fresh,
                SourceTimestamp.FromUtc(ObservationTime.AddSeconds(sourcePosition)));
            var cut = RuntimeCut.Normalize(binding, sourcePosition, [observation]);
            Assert.True(cut.IsSuccess);
            var obligation = await store.AppendCutAsync(cut.Value);
            var acceptance = runtime.Apply(cut.Value);
            Assert.True(acceptance.IsSuccess);
            Assert.True((await store.SaveCheckpointWithPendingDeliveryAsync(
                runtime.CaptureCheckpoint(),
                obligation,
                acceptance.Value,
                protectedContinuity: true)).IsSuccess);
            Assert.True((await store.CompleteDownstreamAsync(
                ScopeId,
                obligation.Position)).IsSuccess);
            Assert.True((await store.PublishCompletedDeliveryAsync(
                ScopeId,
                obligation.Position,
                retainedDeltaCapacity,
                ready: true)).IsSuccess);
        }
    }

    private static SessionSnapshot CreateSession(PointId allowedPoint)
    {
        var now = DateTimeOffset.UtcNow;
        return new SessionSnapshot(
            SessionId.New(),
            SubjectId.New(),
            PrincipalKind.User,
            now.AddMinutes(-1),
            now.AddHours(1),
            new EffectivePermissions([
                RuntimePermissions.ReadCurrent,
                RuntimePermissions.ReadPoint(allowedPoint),
            ]));
    }
}
