using System.Buffers.Binary;
using System.Net.Sockets;
using Dispatcher.Core;
using Dispatcher.Protocols;
using Dispatcher.Semantics;

namespace Dispatcher.Modbus;

public interface IModbusTcpConnection : IAsyncDisposable
{
    ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        int maxResponseBytes,
        CancellationToken cancellationToken);
}

public interface IModbusTcpConnectionFactory
{
    ValueTask<IModbusTcpConnection> OpenAsync(
        string host,
        int port,
        CancellationToken cancellationToken);
}

public sealed class TcpModbusConnectionFactory : IModbusTcpConnectionFactory
{
    public async ValueTask<IModbusTcpConnection> OpenAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new TcpModbusConnection(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed class TcpModbusConnection : IModbusTcpConnection
    {
        private readonly TcpClient client;
        private readonly NetworkStream stream;

        public TcpModbusConnection(TcpClient client)
        {
            this.client = client;
            stream = client.GetStream();
        }

        public async ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> request,
            int maxResponseBytes,
            CancellationToken cancellationToken)
        {
            await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            var header = new byte[7];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            var frameLength = checked(6 + length);
            if (length < 2 || frameLength > maxResponseBytes)
            {
                throw new InvalidDataException("Modbus response length is outside the bounded frame size.");
            }

            var frame = new byte[frameLength];
            header.CopyTo(frame, 0);
            await stream.ReadExactlyAsync(frame.AsMemory(7), cancellationToken).ConfigureAwait(false);
            return frame;
        }

        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
        }
    }
}

public sealed class ModbusTcpTransport : IReadOnlyProtocolTransport
{
    private readonly ModbusTcpSourceConfiguration configuration;
    private readonly IModbusTcpConnectionFactory connectionFactory;

    public ModbusTcpTransport(
        ModbusTcpSourceConfiguration configuration,
        IModbusTcpConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        this.configuration = configuration;
        this.connectionFactory = connectionFactory;
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(
        ProtocolTransportRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Binding.SourceId != configuration.SourceId)
        {
            throw new InvalidOperationException("Modbus transport request belongs to another source.");
        }

        if (request.Secret is not null)
        {
            throw new UnauthorizedAccessException("The Modbus TCP read-only profile does not accept a secret.");
        }

        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= configuration.RetryPolicy.MaxAttempts; attempt++)
        {
            try
            {
                await using var connection = await connectionFactory.OpenAsync(
                    configuration.Host,
                    configuration.Port,
                    cancellationToken).ConfigureAwait(false);
                if (request.Purpose == ProtocolReadPurpose.Diagnostic &&
                    request.DiagnosticMode == ProtocolDiagnosticMode.ConnectionTest)
                {
                    return ModbusBatchCodec.Encode([]);
                }

                var frames = new ReadOnlyMemory<byte>[configuration.Points.Count];
                var totalBytes = 2;
                for (var index = 0; index < configuration.Points.Count; index++)
                {
                    var frame = await connection.ExchangeAsync(
                        ModbusTcpFrameCodec.BuildReadRequest(
                            checked((ushort)(index + 1)),
                            configuration.UnitId,
                            configuration.Points[index]),
                        request.MaxResponseBytes,
                        cancellationToken).ConfigureAwait(false);
                    totalBytes = checked(totalBytes + 2 + frame.Length);
                    if (totalBytes > request.MaxResponseBytes)
                    {
                        throw new InvalidDataException("Modbus response batch exceeds the configured byte limit.");
                    }

                    frames[index] = frame;
                }

                return ModbusBatchCodec.Encode(frames);
            }
            catch (Exception exception) when (
                exception is IOException or SocketException &&
                !cancellationToken.IsCancellationRequested)
            {
                lastFailure = exception;
                if (attempt < configuration.RetryPolicy.MaxAttempts && configuration.RetryPolicy.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(configuration.RetryPolicy.Delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new IOException("Modbus TCP read failed after the bounded retry policy.", lastFailure);
    }
}

public sealed class ModbusTcpSource : IDisposable
{
    private ModbusTcpSource(ProtocolSourceController controller) => Controller = controller;

    public ProtocolSourceController Controller { get; }

    public static Result<ModbusTcpSource> Create(
        ModbusTcpSourceConfiguration configuration,
        ModbusConfigurationLimits configurationLimits,
        ProtocolWorkloadIdentity workloadIdentity,
        ProtocolIoLimits ioLimits,
        IModbusTcpConnectionFactory connectionFactory,
        IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var validation = configuration.Validate(configurationLimits);
        if (validation.IsFailure)
        {
            return Result.Failure<ModbusTcpSource>(validation.Error!);
        }

        var parser = new ModbusObservationParser(configuration, clock);
        return Result.Success(new ModbusTcpSource(new ProtocolSourceController(
            workloadIdentity,
            new ModbusTcpTransport(configuration, connectionFactory),
            parser,
            new NoModbusSecretResolver(),
            ioLimits,
            parser)));
    }

    public Task<Result<RuntimeCut>> AcquireAsync(
        ProtocolSourceRequest request,
        CancellationToken cancellationToken = default) =>
        Controller.AcquireAsync(request, cancellationToken);

    public Task<Result<ProtocolDiagnosticResult>> ConnectionTestAsync(
        SourceBinding binding,
        CancellationToken cancellationToken = default) =>
        Controller.DiagnoseAsync(
            new ProtocolDiagnosticRequest(binding, null, ProtocolDiagnosticMode.ConnectionTest),
            cancellationToken);

    public Task<Result<ProtocolDiagnosticResult>> SamplePollAsync(
        SourceBinding binding,
        CancellationToken cancellationToken = default) =>
        Controller.DiagnoseAsync(
            new ProtocolDiagnosticRequest(binding, null, ProtocolDiagnosticMode.SamplePoll),
            cancellationToken);

    public void Dispose() => Controller.Dispose();

    private sealed class NoModbusSecretResolver : IProtocolSecretResolver
    {
        public ValueTask<ProtocolSecretLease> ResolveAsync(
            ProtocolSecretReference reference,
            ProtocolWorkloadIdentity workloadIdentity,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<ProtocolSecretLease>(
                new UnauthorizedAccessException("Modbus TCP read-only does not use a secret."));
    }
}
