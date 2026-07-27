using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Dispatcher.Web;

public sealed class RealtimeWidgetClient : IAsyncDisposable
{
    private readonly NavigationManager? navigation;
    private readonly IdentitySessionState? identity;
    private readonly RealtimeWidgetState state = new();
    private HubConnection? connection;
    private ulong connectionGeneration;
    private Guid scopeId;
    private IReadOnlyCollection<Guid>? pointIds;

    public RealtimeWidgetClient(NavigationManager navigation, IdentitySessionState identity)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentNullException.ThrowIfNull(identity);
        this.navigation = navigation;
        this.identity = identity;
        identity.Changed += OnSessionChanged;
    }

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
        await EnsureConnectionAsync(cancellationToken);
        await ResnapshotAsync(cancellationToken);
    }

    public async Task PollAsync(CancellationToken cancellationToken)
    {
        var previousGeneration = connectionGeneration;
        await EnsureConnectionAsync(cancellationToken);
        if (identity is not null && previousGeneration != connectionGeneration)
        {
            await ResnapshotAsync(cancellationToken);
            return;
        }

        var activeConnection = connection!;
        if (activeConnection.State == HubConnectionState.Disconnected)
        {
            await activeConnection.StartAsync(cancellationToken);
            await ResnapshotAsync(cancellationToken);
            return;
        }

        var poll = await activeConnection.InvokeAsync<RealtimePollPayload>(
            "Poll",
            scopeId,
            state.Cursor,
            cancellationToken);
        if (poll.Kind == RealtimePollKind.Gap && GapDetected is not null)
        {
            await GapDetected.Invoke();
        }

        state.ApplyPoll(poll);
        if (state.PermissionInvalidated && identity is not null)
        {
            identity.Clear();
        }
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
        if (identity is not null)
        {
            identity.Changed -= OnSessionChanged;
        }

        if (connection is not null)
        {
            connection.Closed -= OnClosedAsync;
            await connection.DisposeAsync();
        }
    }

    private async Task ResnapshotAsync(CancellationToken cancellationToken)
    {
        var activeConnection = connection
            ?? throw new InvalidOperationException("The realtime connection is not available.");
        var snapshot = pointIds is null
            ? await activeConnection.InvokeAsync<RuntimeSnapshotPayload>(
                "Bootstrap",
                scopeId,
                cancellationToken)
            : await activeConnection.InvokeAsync<RuntimeSnapshotPayload>(
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

    private async Task EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (identity is null)
        {
            if (connection!.State == HubConnectionState.Disconnected)
            {
                await connection.StartAsync(cancellationToken);
            }
            return;
        }

        var session = identity.Session
            ?? throw new InvalidOperationException("An authenticated session is required.");
        if (connection is null || connectionGeneration != identity.Generation)
        {
            if (connection is not null)
            {
                connection.Closed -= OnClosedAsync;
                await connection.DisposeAsync();
            }

            connection = new HubConnectionBuilder()
                .WithUrl(new Uri(new Uri(navigation!.BaseUri), "hubs/runtime"), options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.Headers["Authorization"] =
                        $"Dispatcher-Session {session.AccessToken}";
                })
                .Build();
            connection.Closed += OnClosedAsync;
            connectionGeneration = identity.Generation;
        }

        if (connection.State == HubConnectionState.Disconnected)
        {
            await connection.StartAsync(cancellationToken);
        }
    }

    private void OnSessionChanged() => state.InvalidateSession();
}
