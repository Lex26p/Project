using Dispatcher.Core;
using Dispatcher.Modbus;
using Dispatcher.Protocols;
using Dispatcher.Semantics;
using Xunit;

namespace Dispatcher.IntegrationTests;

public sealed class ModbusTcpRuntimeTests
{
    private static readonly RuntimeScopeId ScopeId =
        RuntimeScopeId.From(Guid.Parse("f1000000-0000-0000-0000-000000000001"));
    private static readonly SourceId SourceId =
        SourceId.From(Guid.Parse("f2000000-0000-0000-0000-000000000001"));
    private static readonly ProtocolWorkloadIdentity Workload =
        ProtocolWorkloadIdentity.From("runtime-c12-wire");

    [Fact]
    public async Task LoopbackPeerSeesOnlyFc03AndFc04AndValuesUseEndianAndScale()
    {
        await using var peer = new FakeModbusTcpPeer(
            (request, _, _) =>
            {
                var data = request.Span[7] == 3
                    ? new byte[] { 0, 21 }
                    : new byte[] { 0x78, 0x56, 0x34, 0x12 };
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    FakeModbusTcpPeer.Response(request.Span, data));
            });
        var points = new[]
        {
            Point(1, ModbusRegisterTable.HoldingRegisters, ModbusValueType.Unsigned16, 10)
                with { Scale = 2m },
            Point(2, ModbusRegisterTable.InputRegisters, ModbusValueType.Unsigned32, 20)
                with
                {
                    ByteOrder = ModbusByteOrder.LittleEndian,
                    WordOrder = ModbusWordOrder.LowWordFirst,
                },
        };
        using var source = CreateSource(peer.Port, points, TimeSpan.FromSeconds(1));

        var result = await source.AcquireAsync(
            new ProtocolSourceRequest(Binding(1), 1, null));

        Assert.True(result.IsSuccess, result.Error?.Code.Value);
        Assert.Equal([3, 4], peer.FunctionCodes);
        Assert.Equal(
            [42L, 0x12345678L],
            result.Value.Observations.Select(item => item.Value.Value));
    }

    [Fact]
    public async Task LoopbackShortResponseAndTimeoutAreBounded()
    {
        await using var shortPeer = new FakeModbusTcpPeer(
            (_, _, _) =>
                ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    new byte[] { 0, 1, 0 }));
        using var shortSource = CreateSource(
            shortPeer.Port,
            [Point(1, ModbusRegisterTable.HoldingRegisters, ModbusValueType.Unsigned16, 10)],
            TimeSpan.FromSeconds(1));
        var shortResult = await shortSource.AcquireAsync(
            new ProtocolSourceRequest(Binding(1), 1, null));

        await using var timeoutPeer = new FakeModbusTcpPeer(
            async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            });
        using var timeoutSource = CreateSource(
            timeoutPeer.Port,
            [Point(1, ModbusRegisterTable.HoldingRegisters, ModbusValueType.Unsigned16, 10)],
            TimeSpan.FromMilliseconds(50));
        var timeoutResult = await timeoutSource.AcquireAsync(
            new ProtocolSourceRequest(Binding(1), 1, null));

        Assert.Equal("protocol.io_failed", shortResult.Error?.Code.Value);
        Assert.Equal("protocol.io_timeout", timeoutResult.Error?.Code.Value);
        Assert.All(
            shortPeer.FunctionCodes.Concat(timeoutPeer.FunctionCodes),
            code => Assert.Contains(code, new byte[] { 3, 4 }));
    }

    [Fact]
    public async Task LoopbackDisconnectReconnectsInsideConfiguredAttemptLimit()
    {
        await using var peer = new FakeModbusTcpPeer(
            (request, number, _) =>
                ValueTask.FromResult<ReadOnlyMemory<byte>?>(
                    number == 1
                        ? null
                        : FakeModbusTcpPeer.Response(request.Span, 0, 42)));
        var configuration = Configuration(
            peer.Port,
            [Point(1, ModbusRegisterTable.HoldingRegisters, ModbusValueType.Unsigned16, 10)])
            with
            {
                RetryPolicy = new ModbusRetryPolicy(2, TimeSpan.Zero),
            };
        using var source = ModbusTcpSource.Create(
            configuration,
            new ModbusConfigurationLimits(4, 8),
            Workload,
            new ProtocolIoLimits(TimeSpan.FromSeconds(1), 1024, 4, 1),
            new TcpModbusConnectionFactory(),
            new SystemClock(TimeProvider.System)).Value;

        var result = await source.AcquireAsync(
            new ProtocolSourceRequest(Binding(1), 1, null));

        Assert.True(result.IsSuccess, result.Error?.Code.Value);
        Assert.Equal(42L, Assert.Single(result.Value.Observations).Value.Value);
        Assert.Equal([3, 3], peer.FunctionCodes);
    }

    private static ModbusTcpSource CreateSource(
        int port,
        IReadOnlyList<ModbusPointConfiguration> points,
        TimeSpan timeout) =>
        ModbusTcpSource.Create(
            Configuration(port, points),
            new ModbusConfigurationLimits(4, 8),
            Workload,
            new ProtocolIoLimits(timeout, 1024, 4, 1),
            new TcpModbusConnectionFactory(),
            new SystemClock(TimeProvider.System)).Value;

    private static ModbusTcpSourceConfiguration Configuration(
        int port,
        IReadOnlyList<ModbusPointConfiguration> points) =>
        new(
            ModbusRuntimeProfile.ProductionReadOnly,
            SourceId,
            "127.0.0.1",
            port,
            7,
            points,
            new ModbusRetryPolicy(1, TimeSpan.Zero));

    private static ModbusPointConfiguration Point(
        int number,
        ModbusRegisterTable table,
        ModbusValueType type,
        int address) =>
        new(
            PointId.From(Guid.Parse($"f3000000-0000-0000-0000-{number:D12}")),
            table,
            address,
            type,
            ModbusByteOrder.BigEndian,
            ModbusWordOrder.HighWordFirst,
            Unit.FromSymbol("kW"));

    private static SourceBinding Binding(ulong generation) =>
        new(
            ScopeId,
            SourceId,
            SourceBindingGeneration.From(generation),
            SourceSessionGeneration.From(1));
}
