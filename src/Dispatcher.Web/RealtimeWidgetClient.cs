using Microsoft.AspNetCore.SignalR.Client;

namespace Dispatcher.Web;

public sealed class RealtimeWidgetClient : IAsyncDisposable
{
    private readonly HubConnection connection;
    private readonly RealtimeWidgetState state = new();
    private Guid scopeId;

    public RealtimeWidgetClient(HubConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        this.connection = connection;
        connection.Closed += OnClosedAsync;
    }

    public event Func<Task>? RenderRequested;

    public RealtimeWidgetState State => state;

    public async Task StartAsync(Guid runtimeScopeId, CancellationToken cancellationToken)
    {
        scopeId = runtimeScopeId;
        if (connection.State == HubConnectionState.Disconnected)
        {
            await connection.StartAsync(cancellationToken);
        }

        await ResnapshotAsync(cancellationToken);
    }

    public async Task PollAsync(CancellationToken cancellationToken)
    {
        if (connection.State == HubConnectionState.Disconnected)
        {
            await connection.StartAsync(cancellationToken);
            await ResnapshotAsync(cancellationToken);
            return;
        }

        var poll = await connection.InvokeAsync<RealtimePollPayload>(
            "Poll",
            scopeId,
            state.Cursor,
            cancellationToken);
        state.ApplyPoll(poll);
        if (state.NeedsResync && !state.PermissionInvalidated)
        {
            await ResnapshotAsync(cancellationToken);
        }
        else
        {
            await RequestRenderIfNeededAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        connection.Closed -= OnClosedAsync;
        await connection.DisposeAsync();
    }

    private async Task ResnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await connection.InvokeAsync<RuntimeSnapshotPayload>(
            "Bootstrap",
            scopeId,
            cancellationToken);
        state.ApplySnapshot(snapshot);
        await RequestRenderIfNeededAsync();
    }

    private async Task RequestRenderIfNeededAsync()
    {
        if (state.ConsumeRenderRequest() && RenderRequested is not null)
        {
            await RenderRequested.Invoke();
        }
    }

    private Task OnClosedAsync(Exception? exception)
    {
        state.MarkDisconnected();
        return Task.CompletedTask;
    }
}
