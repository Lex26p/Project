using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Dispatcher.Core;
using Dispatcher.Protocols;
using Dispatcher.Semantics;

namespace Dispatcher.Snmp;

public interface ISnmpDatagramClient : IAsyncDisposable
{
    ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        int maxResponseBytes,
        CancellationToken cancellationToken);
}

public interface ISnmpDatagramClientFactory
{
    ValueTask<ISnmpDatagramClient> OpenAsync(
        string host,
        int port,
        CancellationToken cancellationToken);
}

public sealed class UdpSnmpDatagramClientFactory : ISnmpDatagramClientFactory
{
    public async ValueTask<ISnmpDatagramClient> OpenAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(candidate =>
                          candidate.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                      ?? throw new SocketException((int)SocketError.HostNotFound);
        var client = new UdpClient(address.AddressFamily);
        try
        {
            client.Connect(new IPEndPoint(address, port));
            return new UdpSnmpDatagramClient(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed class UdpSnmpDatagramClient : ISnmpDatagramClient
    {
        private readonly UdpClient client;

        public UdpSnmpDatagramClient(UdpClient client) => this.client = client;

        public async ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> request,
            int maxResponseBytes,
            CancellationToken cancellationToken)
        {
            await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var response = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            return response.Buffer.Length <= maxResponseBytes
                ? response.Buffer
                : throw new InvalidDataException("SNMP datagram exceeds the configured response byte bound.");
        }

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class SnmpV2cTransport : IReadOnlyProtocolTransport
{
    private readonly SnmpV2cSourceConfiguration configuration;
    private readonly SnmpWireLimits wireLimits;
    private readonly ISnmpDatagramClientFactory clientFactory;
    private int nextRequestId;

    public SnmpV2cTransport(
        SnmpV2cSourceConfiguration configuration,
        SnmpWireLimits wireLimits,
        ISnmpDatagramClientFactory clientFactory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(wireLimits);
        ArgumentNullException.ThrowIfNull(clientFactory);
        this.configuration = configuration;
        this.wireLimits = wireLimits;
        this.clientFactory = clientFactory;
    }

    public ValueTask<ReadOnlyMemory<byte>> ReadAsync(
        ProtocolTransportRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Binding.SourceId != configuration.SourceId)
        {
            return ValueTask.FromException<ReadOnlyMemory<byte>>(
                new InvalidOperationException("SNMP transport request belongs to another source."));
        }

        if (request.Secret is null)
        {
            return ValueTask.FromException<ReadOnlyMemory<byte>>(
                new UnauthorizedAccessException("SNMP v2c requires a community secret lease."));
        }

        return request.Secret.UseAsync(
            (community, token) => ExchangeWithCommunityAsync(request, EncodeCommunity(community), token),
            cancellationToken);
    }

    private async ValueTask<ReadOnlyMemory<byte>> ExchangeWithCommunityAsync(
        ProtocolTransportRequest request,
        byte[] community,
        CancellationToken cancellationToken)
    {
        try
        {
            var requestId = NextRequestId();
            var oids = request.Purpose == ProtocolReadPurpose.Diagnostic &&
                       request.DiagnosticMode == ProtocolDiagnosticMode.ConnectionTest
                ? new[] { configuration.Points[0].Oid }
                : configuration.Points.Select(point => point.Oid).ToArray();
            var encoded = SnmpV2cCodec.BuildGetRequest(requestId, community, oids, wireLimits);
            if (encoded.IsFailure)
            {
                throw new InvalidDataException(encoded.Error!.Code.Value);
            }

            Exception? lastFailure = null;
            for (var attempt = 1; attempt <= configuration.RetryPolicy.MaxAttempts; attempt++)
            {
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptTimeout.CancelAfter(configuration.RetryPolicy.ResponseTimeout);
                try
                {
                    await using var client = await clientFactory.OpenAsync(
                        configuration.Host,
                        configuration.Port,
                        attemptTimeout.Token).ConfigureAwait(false);
                    var response = await client.ExchangeAsync(
                        encoded.Value,
                        request.MaxResponseBytes - 4,
                        attemptTimeout.Token).ConfigureAwait(false);
                    if (!SnmpV2cCodec.ResponseCommunityMatches(response, community))
                    {
                        throw new InvalidDataException("SNMP response community does not match the resolved reference.");
                    }

                    return SnmpV2cCodec.WrapResponse(requestId, response);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    lastFailure = exception;
                }
                catch (Exception exception) when (
                    exception is IOException or SocketException &&
                    !cancellationToken.IsCancellationRequested)
                {
                    lastFailure = exception;
                }

                if (attempt < configuration.RetryPolicy.MaxAttempts && configuration.RetryPolicy.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(configuration.RetryPolicy.Delay, cancellationToken).ConfigureAwait(false);
                }
            }

            if (lastFailure is OperationCanceledException)
            {
                throw new OperationCanceledException(
                    "SNMP response deadline elapsed after the bounded retry policy.",
                    lastFailure,
                    cancellationToken);
            }

            throw new IOException("SNMP read failed after the bounded retry policy.", lastFailure);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(community);
        }
    }

    private int NextRequestId()
    {
        var value = Interlocked.Increment(ref nextRequestId);
        if (value > 0)
        {
            return value;
        }

        Interlocked.Exchange(ref nextRequestId, 1);
        return 1;
    }

    private static byte[] EncodeCommunity(ReadOnlyMemory<char> community)
    {
        var encoded = new byte[Encoding.UTF8.GetByteCount(community.Span)];
        Encoding.UTF8.GetBytes(community.Span, encoded);
        return encoded;
    }
}

public sealed class SnmpV2cSource : IDisposable
{
    private readonly SnmpV2cSourceConfiguration configuration;

    private SnmpV2cSource(
        SnmpV2cSourceConfiguration configuration,
        ProtocolSourceController controller)
    {
        this.configuration = configuration;
        Controller = controller;
    }

    public ProtocolSourceController Controller { get; }

    public static Result<SnmpV2cSource> Create(
        SnmpV2cSourceConfiguration configuration,
        SnmpConfigurationLimits configurationLimits,
        SnmpWireLimits wireLimits,
        ProtocolWorkloadIdentity workloadIdentity,
        ProtocolIoLimits ioLimits,
        IProtocolSecretResolver secretResolver,
        ISnmpDatagramClientFactory clientFactory,
        IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var validation = configuration.Validate(configurationLimits);
        if (validation.IsFailure)
        {
            return Result.Failure<SnmpV2cSource>(validation.Error!);
        }

        if (ioLimits.MaxResponseBytes <= 4)
        {
            return Result.Failure<SnmpV2cSource>(new OperationError(
                ErrorCode.From("snmp.response_limit"),
                "SNMP response limit cannot contain the bounded response envelope."));
        }

        var parser = new SnmpObservationParser(configuration, clock);
        return Result.Success(new SnmpV2cSource(
            configuration,
            new ProtocolSourceController(
                workloadIdentity,
                new SnmpV2cTransport(configuration, wireLimits, clientFactory),
                parser,
                secretResolver,
                ioLimits,
                parser)));
    }

    public Task<Result<RuntimeCut>> AcquireAsync(
        SourceBinding binding,
        ulong scheduleSequence,
        CancellationToken cancellationToken = default) =>
        Controller.AcquireAsync(
            new ProtocolSourceRequest(binding, scheduleSequence, configuration.CommunityReference),
            cancellationToken);

    public Task<Result<ProtocolDiagnosticResult>> ConnectionTestAsync(
        SourceBinding binding,
        CancellationToken cancellationToken = default) =>
        Controller.DiagnoseAsync(
            new ProtocolDiagnosticRequest(
                binding,
                configuration.CommunityReference,
                ProtocolDiagnosticMode.ConnectionTest),
            cancellationToken);

    public Task<Result<ProtocolDiagnosticResult>> SamplePollAsync(
        SourceBinding binding,
        CancellationToken cancellationToken = default) =>
        Controller.DiagnoseAsync(
            new ProtocolDiagnosticRequest(
                binding,
                configuration.CommunityReference,
                ProtocolDiagnosticMode.SamplePoll),
            cancellationToken);

    public void Dispose() => Controller.Dispose();
}
