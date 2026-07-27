using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Dispatcher.IntegrationTests.ProcessTests;

internal sealed class TcpFaultProxy : IAsyncDisposable
{
    private readonly IPAddress targetAddress;
    private readonly int targetPort;
    private readonly object sync = new();
    private readonly ConcurrentDictionary<int, ConnectionPair> connections = new();
    private readonly ConcurrentDictionary<Task, byte> connectionTasks = new();
    private TcpListener? listener;
    private CancellationTokenSource? generation;
    private Task? acceptLoop;
    private int connectionId;

    private TcpFaultProxy(IPAddress targetAddress, int targetPort)
    {
        this.targetAddress = targetAddress;
        this.targetPort = targetPort;
    }

    public int Port { get; private set; }

    public static TcpFaultProxy Start(IPAddress targetAddress, int targetPort)
    {
        var proxy = new TcpFaultProxy(targetAddress, targetPort);
        proxy.Resume();
        return proxy;
    }

    public void Resume()
    {
        lock (sync)
        {
            if (listener is not null)
            {
                return;
            }

            var next = new TcpListener(IPAddress.Loopback, Port);
            next.Server.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true);
            next.Start();
            if (Port == 0)
            {
                Port = ((IPEndPoint)next.LocalEndpoint).Port;
            }
            generation = new CancellationTokenSource();
            listener = next;
            acceptLoop = AcceptAsync(next, generation.Token);
        }
    }

    public async Task PauseAsync()
    {
        TcpListener? currentListener;
        CancellationTokenSource? currentGeneration;
        Task? currentAcceptLoop;
        lock (sync)
        {
            currentListener = listener;
            currentGeneration = generation;
            currentAcceptLoop = acceptLoop;
            listener = null;
            generation = null;
            acceptLoop = null;
        }

        if (currentListener is null)
        {
            return;
        }

        currentGeneration!.Cancel();
        currentListener.Stop();
        foreach (var pair in connections.Values)
        {
            pair.Dispose();
        }

        await IgnoreCancellationAsync(currentAcceptLoop!).ConfigureAwait(false);
        await Task.WhenAll(connectionTasks.Keys).WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        currentGeneration.Dispose();
    }

    public async ValueTask DisposeAsync() => await PauseAsync().ConfigureAwait(false);

    private async Task AcceptAsync(TcpListener current, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var client = await current.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                var task = ForwardAsync(client, cancellationToken);
                connectionTasks.TryAdd(task, 0);
                _ = task.ContinueWith(
                    completed => connectionTasks.TryRemove(completed, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ForwardAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var backend = new TcpClient();
        var id = Interlocked.Increment(ref connectionId);
        var pair = new ConnectionPair(client, backend);
        connections.TryAdd(id, pair);
        try
        {
            client.NoDelay = true;
            backend.NoDelay = true;
            await backend.ConnectAsync(targetAddress, targetPort, cancellationToken).ConfigureAwait(false);
            await using var clientStream = client.GetStream();
            await using var backendStream = backend.GetStream();
            var upstream = clientStream.CopyToAsync(backendStream, cancellationToken);
            var downstream = backendStream.CopyToAsync(clientStream, cancellationToken);
            await Task.WhenAny(upstream, downstream).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or IOException or SocketException)
        {
        }
        finally
        {
            connections.TryRemove(id, out _);
            pair.Dispose();
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class ConnectionPair(TcpClient client, TcpClient backend) : IDisposable
    {
        public void Dispose()
        {
            client.Dispose();
            backend.Dispose();
        }
    }
}
