using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Dispatcher.IntegrationTests;

internal sealed class FakeModbusTcpPeer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> respond;
    private readonly ConcurrentQueue<byte> functionCodes = [];
    private readonly Task loop;
    private int requestCount;

    public FakeModbusTcpPeer(
        Func<ReadOnlyMemory<byte>, int, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> respond)
    {
        this.respond = respond ?? throw new ArgumentNullException(nameof(respond));
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        loop = RunAsync(cancellation.Token);
    }

    public int Port { get; }

    public IReadOnlyList<byte> FunctionCodes => functionCodes.ToArray();

    public static ReadOnlyMemory<byte> Response(
        ReadOnlySpan<byte> request,
        params byte[] data)
    {
        var response = new byte[9 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(
            response,
            BinaryPrimitives.ReadUInt16BigEndian(request));
        BinaryPrimitives.WriteUInt16BigEndian(
            response.AsSpan(4),
            checked((ushort)(3 + data.Length)));
        response[6] = request[6];
        response[7] = request[7];
        response[8] = checked((byte)data.Length);
        data.CopyTo(response, 9);
        return response;
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        listener.Stop();
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }

        cancellation.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                var request = new byte[12];
                try
                {
                    await stream.ReadExactlyAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                functionCodes.Enqueue(request[7]);
                var response = await respond(
                    request,
                    Interlocked.Increment(ref requestCount),
                    cancellationToken).ConfigureAwait(false);
                if (response is not { } bytes)
                {
                    break;
                }

                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (bytes.Length < 7)
                {
                    break;
                }
            }
        }
    }
}
