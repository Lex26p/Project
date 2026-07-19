using Microsoft.AspNetCore.SignalR.Client;

namespace Dispatcher.Web;

public sealed class RealtimeWidgetClient : IAsyncDisposable
{
    private readonly HubConnection connection;
    private readonly RealtimeWidgetState state = new();
    private Guid scopeId;
    private IReadOnlyCollection<Guid>? pointIds;

    public RealtimeWidgetClient(HubConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        this.connection = connection;
        connection.Closed += OnClosedAsync;
    }

    public event Func<Task>? RenderRequested;

    public event Func<Task>? GapDetected;

    public RealtimeWidgetState State => state;

    public async Task StartAsync(
        Guid runtimeScopeId,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? requestedPointIds = null)
    {
        scopeId = runtimeScopeId;
        pointIds = requestedPointIds;
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
        if (poll.Kind == RealtimePollKind.Gap && GapDetected is not null)
        {
            await GapDetected.Invoke();
        }

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
        var snapshot = pointIds is null
            ? await connection.InvokeAsync<RuntimeSnapshotPayload>(
                "Bootstrap",
                scopeId,
                cancellationToken)
            : await connection.InvokeAsync<RuntimeSnapshotPayload>(
                "BootstrapPoints",
                scopeId,
                pointIds,
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
