using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Dispatcher.Core;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Server;
using Dispatcher.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using WebPollKind = Dispatcher.Web.RealtimePollKind;
using WebPollPayload = Dispatcher.Web.RealtimePollPayload;
using WebSnapshotPayload = Dispatcher.Web.RuntimeSnapshotPayload;

namespace Dispatcher.IntegrationTests;

public sealed class ServerRealtimeTests
{
    private static readonly DateTimeOffset ObservationTime =
        new(2026, 7, 19, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AuthorizedHttpAndRealtimeExposeOnlyAllowedPointAndCatchUpSlowConsumer()
    {
        await using var host = await RealtimeHost.StartAsync();
        using var http = host.CreateHttpClient();

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

        host.AdmitAllowed(11, 2);
        host.AdmitAllowed(12, 3);
        host.AdmitAllowed(13, 4);
        host.AdmitHidden(99, 2);

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
        await using var host = await RealtimeHost.StartAsync();
        await using var hub = host.CreateHubConnection();
        await hub.StartAsync();
        var snapshot = await hub.InvokeAsync<WebSnapshotPayload>("Bootstrap", host.ScopeId.Value);

        var gap = await hub.InvokeAsync<WebPollPayload>("Poll", host.ScopeId.Value, snapshot.Cursor + 1);
        Assert.Equal(WebPollKind.Gap, gap.Kind);
        var resnapshot = await hub.InvokeAsync<WebSnapshotPayload>("Bootstrap", host.ScopeId.Value);
        Assert.Equal(host.AllowedPoint.Value, Assert.Single(resnapshot.Points).PointId);

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

    private sealed class RealtimeHost : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly SessionSnapshot session;
        private readonly SourceId allowedSource = SourceId.From(
            Guid.Parse("30000000-0000-0000-0000-000000000001"));
        private readonly SourceId hiddenSource = SourceId.From(
            Guid.Parse("30000000-0000-0000-0000-000000000002"));
        private readonly CoreRuntime runtime;

        private RealtimeHost(
            WebApplication app,
            Uri address,
            SessionSnapshot session,
            RuntimeScopeId scopeId,
            PointId allowedPoint,
            PointId hiddenPoint,
            CoreRuntime runtime)
        {
            this.app = app;
            Address = address;
            this.session = session;
            ScopeId = scopeId;
            AllowedPoint = allowedPoint;
            HiddenPoint = hiddenPoint;
            this.runtime = runtime;
        }

        public Uri Address { get; }

        public RuntimeScopeId ScopeId { get; }

        public PointId AllowedPoint { get; }

        public PointId HiddenPoint { get; }

        public static async Task<RealtimeHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Test",
            });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddDispatcherServer(builder.Configuration);
            builder.Services.Configure<TestSessionBridgeOptions>(options => options.Enabled = true);
            var app = builder.Build();
            app.MapDispatcherServer();

            var scopeId = RuntimeScopeId.From(Guid.Parse("10000000-0000-0000-0000-000000000001"));
            var allowedPoint = PointId.From(Guid.Parse("20000000-0000-0000-0000-000000000001"));
            var hiddenPoint = PointId.From(Guid.Parse("20000000-0000-0000-0000-000000000002"));
            var runtime = new CoreRuntime(scopeId, SystemClock.Instance, SystemClock.Instance);
            app.Services.GetRequiredService<RuntimeRegistry>().Add(scopeId, runtime);

            var now = DateTimeOffset.UtcNow;
            var session = new SessionSnapshot(
                SessionId.New(),
                SubjectId.New(),
                PrincipalKind.User,
                now.AddMinutes(-1),
                now.AddHours(1),
                new EffectivePermissions([
                    RuntimePermissions.ReadCurrent,
                    RuntimePermissions.ReadPoint(allowedPoint),
                ]));
            app.Services.GetRequiredService<SessionDirectory>().Set(session);

            await app.StartAsync();
            var addresses = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!;
            var address = new Uri(Assert.Single(addresses.Addresses));
            var host = new RealtimeHost(app, address, session, scopeId, allowedPoint, hiddenPoint, runtime);
            host.AdmitAllowed(10, 1);
            host.AdmitHidden(90, 1);
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

        public void AdmitAllowed(long value, ulong sourcePosition) =>
            Admit(allowedSource, AllowedPoint, value, sourcePosition);

        public void AdmitHidden(long value, ulong sourcePosition) =>
            Admit(hiddenSource, HiddenPoint, value, sourcePosition);

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
        }

        private void Admit(SourceId sourceId, PointId pointId, long value, ulong sourcePosition)
        {
            var binding = new SourceBinding(
                ScopeId,
                sourceId,
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1));
            Assert.True(runtime.ActivateBinding(binding).IsSuccess);
            var observation = new SourceObservation(
                ScopeId,
                sourceId,
                pointId,
                new OwnerPosition<SourceObservation>(sourcePosition),
                TypedValue.From(value),
                Unit.FromSymbol("°C"),
                DataQuality.Good,
                Freshness.Fresh,
                SourceTimestamp.FromUtc(ObservationTime.AddSeconds(sourcePosition)));
            var cut = RuntimeCut.Normalize(binding, sourcePosition, [observation]);
            Assert.True(cut.IsSuccess);
            Assert.True(runtime.Apply(cut.Value).IsSuccess);
        }
    }
}
