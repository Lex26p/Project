using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace Dispatcher.Web;

public sealed class CommandRealtimeClient : IAsyncDisposable
{
    private readonly HubConnection connection;
    private Guid scopeId;
    private ulong cursor;

    public CommandRealtimeClient(NavigationManager navigation, IdentitySessionState identity)
    {
        connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(navigation.BaseUri), "hubs/commands"), options =>
            {
                if (identity.Session is not null)
                    options.Headers["Authorization"] = $"Dispatcher-Session {identity.Session.AccessToken}";
            })
            .Build();
    }

    public async Task<CommandExecutionSnapshotPayload> StartAsync(Guid runtimeScopeId, CancellationToken token = default)
    {
        scopeId = runtimeScopeId;
        if (connection.State == HubConnectionState.Disconnected) await connection.StartAsync(token);
        var snapshot = await connection.InvokeAsync<CommandExecutionSnapshotPayload>("Bootstrap", scopeId, token);
        cursor = snapshot.Cursor;
        return snapshot;
    }

    public async Task<CommandExecutionFeedPayload> PollAsync(CancellationToken token = default)
    {
        var feed = await connection.InvokeAsync<CommandExecutionFeedPayload>("Poll", scopeId, cursor, token);
        cursor = feed.To;
        return feed;
    }

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();
}
