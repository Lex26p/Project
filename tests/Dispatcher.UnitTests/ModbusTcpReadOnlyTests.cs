using System.Buffers.Binary;
using Dispatcher.Core;
using Dispatcher.Modbus;
using Dispatcher.Protocols;
using Dispatcher.Semantics;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class ModbusTcpReadOnlyTests
{
    private static readonly RuntimeScopeId ScopeId = RuntimeScopeId.From(
        Guid.Parse("b1000000-0000-0000-0000-000000000001"));
    private static readonly SourceId SourceId = SourceId.From(
        Guid.Parse("b2000000-0000-0000-0000-000000000001"));
    private static readonly PointId PointA = PointId.From(
        Guid.Parse("b3000000-0000-0000-0000-000000000001"));
    private static readonly PointId PointB = PointId.From(
        Guid.Parse("b3000000-0000-0000-0000-000000000002"));
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly ProtocolWorkloadIdentity Workload =
        ProtocolWorkloadIdentity.From("dispatcher-runtime:windows-x64");
    private static readonly ModbusConfigurationLimits ConfigurationLimits = new(16, 32);
    private static readonly ProtocolIoLimits IoLimits = new(TimeSpan.FromSeconds(1), 4096, 16, 1);

    [Fact]
    public void ConfigurationValidatesUnitAddressTypeAndEndian()
    {
        var valid = Configuration([Point(PointA, ModbusValueType.Signed16, 10)]);
        Assert.True(valid.Validate(ConfigurationLimits).IsSuccess);
        Assert.Equal(
            "modbus.unit_id",
            (valid with { UnitId = 256 }).Validate(ConfigurationLimits).Error?.Code.Value);
        Assert.Equal(
            "modbus.address",
            Configuration([Point(PointA, ModbusValueType.Signed32, 65535)]).Validate(ConfigurationLimits).Error?.Code.Value);
        Assert.Equal(
            "modbus.value_type",
            Configuration([Point(PointA, (ModbusValueType)99, 10)]).Validate(ConfigurationLimits).Error?.Code.Value);
        Assert.Equal(
            "modbus.endian",
            Configuration([Point(PointA, ModbusValueType.Signed16, 10) with
            {
                WordOrder = ModbusWordOrder.LowWordFirst,
            }]).Validate(ConfigurationLimits).Error?.Code.Value);
        Assert.Equal(
            "modbus.profile",
            (valid with { Profile = (ModbusRuntimeProfile)99 }).Validate(ConfigurationLimits).Error?.Code.Value);
    }

    [Fact]
    public void RequestSurfaceContainsOnlyReadHoldingAndInputFunctions()
    {
        Assert.Equal(new byte[] { 3, 4 }, ModbusTcpFrameCodec.SupportedFunctionCodes);
        var holding = ModbusTcpFrameCodec.BuildReadRequest(1, 7, Point(PointA, ModbusValueType.Signed16, 12));
        var input = ModbusTcpFrameCodec.BuildReadRequest(2, 7, Point(PointB, ModbusValueType.Signed32, 20) with
        {
            Table = ModbusRegisterTable.InputRegisters,
        });
        Assert.Equal((byte)3, holding[7]);
        Assert.Equal((byte)4, input[7]);
        Assert.DoesNotContain(
            typeof(ModbusTcpSource).Assembly.GetExportedTypes().SelectMany(type => type.GetMethods()),
            method => method.Name.Contains("Write", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AcquisitionDecodesEndianAndRepresentsPartialResponseExplicitly()
    {
        var points = new[]
        {
            Point(PointA, ModbusValueType.Signed16, 10),
            Point(PointB, ModbusValueType.Unsigned32, 20) with
            {
                Table = ModbusRegisterTable.InputRegisters,
                ByteOrder = ModbusByteOrder.LittleEndian,
                WordOrder = ModbusWordOrder.LowWordFirst,
            },
        };
        var connection = new QueueConnection(
            Response(1, 7, 3, 0xFF, 0xFE),
            ExceptionResponse(2, 7, 4, 2));
        using var source = CreateSource(Configuration(points), new QueueConnectionFactory(connection));

        var acquired = await source.AcquireAsync(new ProtocolSourceRequest(Binding(1), 1, null));

        Assert.True(acquired.IsSuccess);
        Assert.Equal(-2, acquired.Value.Observations[0].Value.Value);
        Assert.Equal(DataQuality.Good, acquired.Value.Observations[0].Quality);
        Assert.Equal(DataQuality.Bad, acquired.Value.Observations[1].Quality);
        Assert.Equal(Freshness.Stale, acquired.Value.Observations[1].Freshness);
    }

    [Fact]
    public async Task ConnectionAndSampleDiagnosticsDoNotAdvanceSourceOrBlockValidApply()
    {
        var point = Point(PointA, ModbusValueType.Unsigned32, 20) with
        {
            ByteOrder = ModbusByteOrder.LittleEndian,
            WordOrder = ModbusWordOrder.LowWordFirst,
        };
        var factory = new QueueConnectionFactory(
            new QueueConnection(),
            new QueueConnection(Response(1, 7, 3, 0x78, 0x56, 0x34, 0x12)),
            new QueueConnection(Response(1, 7, 3, 0x78, 0x56, 0x34, 0x12)));
        using var source = CreateSource(Configuration([point]), factory);
        var binding = Binding(1);
        var clock = new FixedClock();
        var core = new CoreRuntime(ScopeId, clock, clock, new RuntimeCurrentLimits(4, 8));
        Assert.True(core.ActivateBinding(binding).IsSuccess);

        var connection = await source.ConnectionTestAsync(binding);
        var sample = await source.SamplePollAsync(binding);
        Assert.Empty(core.GetSnapshot().Entries);
        var acquired = await source.AcquireAsync(new ProtocolSourceRequest(binding, 1, null));

        Assert.True(connection.IsSuccess);
        Assert.Empty(connection.Value.Samples);
        Assert.True(sample.IsSuccess);
        Assert.Equal(0x12345678L, Assert.Single(sample.Value.Samples).Value);
        Assert.False(sample.Value.Partial);
        Assert.Equal((ulong)1, Assert.Single(acquired.Value.Observations).SourcePosition.Value);
        Assert.True(core.Apply(acquired.Value).IsSuccess);
        Assert.Equal(0x12345678L, Assert.Single(core.GetSnapshot().Entries).Value.Value);
    }

    [Fact]
    public async Task DisconnectUsesBoundedRetryAndThenSucceeds()
    {
        var factory = new QueueConnectionFactory(
            new IOException("disconnect"),
            new QueueConnection(Response(1, 7, 3, 0, 42)));
        using var source = CreateSource(
            Configuration([Point(PointA, ModbusValueType.Unsigned16, 10)]) with
            {
                RetryPolicy = new ModbusRetryPolicy(2, TimeSpan.Zero),
            },
            factory);

        var acquired = await source.AcquireAsync(new ProtocolSourceRequest(Binding(1), 1, null));

        Assert.True(acquired.IsSuccess);
        Assert.Equal(2, factory.OpenAttempts);
        Assert.Equal(42, Assert.Single(acquired.Value.Observations).Value.Value);
    }

    [Fact]
    public async Task LateResponseIsFencedAfterBindingGenerationChanges()
    {
        var blocking = new BlockingConnection();
        using var source = CreateSource(
            Configuration([Point(PointA, ModbusValueType.Unsigned16, 10)]),
            new QueueConnectionFactory(blocking));
        var supervisor = new ProtocolRuntimeSupervisor(Workload, 1);
        Assert.True(supervisor.Register(SourceId, source.Controller).IsSuccess);
        var oldBinding = Binding(1);
        Assert.True(supervisor.ActivateBinding(oldBinding).IsSuccess);
        Assert.True(supervisor.Start().IsSuccess);
        var pending = supervisor.AcquireAsync(new ProtocolSourceRequest(oldBinding, 1, null));
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(supervisor.ActivateBinding(Binding(2)).IsSuccess);
        blocking.Release.TrySetResult(Response(1, 7, 3, 0, 42));
        var late = await pending;

        Assert.Equal("protocol.binding_stale", late.Error?.Code.Value);
        Assert.True((await supervisor.StopAsync()).IsSuccess);
    }

    private static ModbusTcpSource CreateSource(
        ModbusTcpSourceConfiguration configuration,
        IModbusTcpConnectionFactory factory) =>
        ModbusTcpSource.Create(
            configuration,
            ConfigurationLimits,
            Workload,
            IoLimits,
            factory,
            new FixedClock()).Value;

    private static ModbusTcpSourceConfiguration Configuration(IReadOnlyList<ModbusPointConfiguration> points) => new(
        ModbusRuntimeProfile.NonProductionReadOnly,
        SourceId,
        "127.0.0.1",
        502,
        7,
        points,
        new ModbusRetryPolicy(1, TimeSpan.Zero));

    private static ModbusPointConfiguration Point(
        PointId pointId,
        ModbusValueType valueType,
        int address) => new(
        pointId,
        ModbusRegisterTable.HoldingRegisters,
        address,
        valueType,
        ModbusByteOrder.BigEndian,
        ModbusWordOrder.HighWordFirst,
        Unit.FromSymbol("kW"));

    private static SourceBinding Binding(ulong generation) => new(
        ScopeId,
        SourceId,
        SourceBindingGeneration.From(generation),
        SourceSessionGeneration.From(1));

    private static byte[] Response(
        ushort transactionId,
        byte unitId,
        byte function,
        params byte[] data)
    {
        var frame = new byte[9 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), checked((ushort)(3 + data.Length)));
        frame[6] = unitId;
        frame[7] = function;
        frame[8] = checked((byte)data.Length);
        data.CopyTo(frame, 9);
        return frame;
    }

    private static byte[] ExceptionResponse(
        ushort transactionId,
        byte unitId,
        byte function,
        byte exception)
    {
        var frame = new byte[9];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 3);
        frame[6] = unitId;
        frame[7] = checked((byte)(function | 0x80));
        frame[8] = exception;
        return frame;
    }

    private sealed class FixedClock : IWallClock, IMonotonicClock
    {
        public DateTimeOffset GetUtcNow() => Now;
        public MonotonicTimestamp GetTimestamp() => new(0);
        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) => TimeSpan.Zero;
    }

    private sealed class QueueConnectionFactory : IModbusTcpConnectionFactory
    {
        private readonly Queue<object> outcomes;

        public QueueConnectionFactory(params object[] outcomes) => this.outcomes = new Queue<object>(outcomes);

        public int OpenAttempts { get; private set; }

        public ValueTask<IModbusTcpConnection> OpenAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            OpenAttempts++;
            var outcome = outcomes.Dequeue();
            return outcome is Exception exception
                ? ValueTask.FromException<IModbusTcpConnection>(exception)
                : ValueTask.FromResult((IModbusTcpConnection)outcome);
        }
    }

    private sealed class QueueConnection : IModbusTcpConnection
    {
        private readonly Queue<ReadOnlyMemory<byte>> responses;

        public QueueConnection(params byte[][] responses) =>
            this.responses = new Queue<ReadOnlyMemory<byte>>(
                responses.Select(response => (ReadOnlyMemory<byte>)response));

        public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> request,
            int maxResponseBytes,
            CancellationToken cancellationToken) => ValueTask.FromResult(responses.Dequeue());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingConnection : IModbusTcpConnection
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ReadOnlyMemory<byte>> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> request,
            int maxResponseBytes,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return await Release.Task.WaitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
