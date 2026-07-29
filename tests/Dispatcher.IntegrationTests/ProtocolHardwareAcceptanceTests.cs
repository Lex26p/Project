using System.Diagnostics;
using Dispatcher.Core;
using Dispatcher.Modbus;
using Dispatcher.Protocols;
using Dispatcher.Semantics;
using Dispatcher.Snmp;
using Xunit;
using Xunit.Abstractions;

namespace Dispatcher.IntegrationTests;

public sealed class ProtocolHardwareAcceptanceTests(ITestOutputHelper output)
{
    private static readonly RuntimeScopeId ScopeId =
        RuntimeScopeId.From(Guid.Parse("c1500000-0000-0000-0000-000000000001"));
    private static readonly ProtocolWorkloadIdentity Workload =
        ProtocolWorkloadIdentity.From("runtime-c15-physical-lab");
    private static readonly ProtocolIoLimits IoLimits =
        new(TimeSpan.FromSeconds(4), 65_536, 16, 1);

    [PhysicalLabFact]
    [Trait("Category", "PhysicalLab")]
    public async Task DeltaUpsReconnectRestoresGoodFreshCurrentWithNewSessionFence()
    {
        var host = Required("DISPATCHER_C15_SNMP_HOST");
        var recoveryPort = OptionalInt("DISPATCHER_C15_SNMP_PORT", 161);
        var unavailablePort = OptionalInt(
            "DISPATCHER_C15_SNMP_UNAVAILABLE_PORT",
            65000);
        var connectionFactory = new SwitchablePortSnmpClientFactory(
            unavailablePort);
        using var source = CreateSnmpSource(
            host,
            recoveryPort,
            sourceNumber: 6,
            [
                SnmpPoint(
                    23,
                    "1.3.6.1.4.1.2254.2.5.4.3.0",
                    "V") with { Scale = 0.1m },
            ],
            connectionFactory);
        var clock = new SystemClock(TimeProvider.System);
        var runtime = new CoreRuntime(
            ScopeId,
            clock,
            clock,
            new RuntimeCurrentLimits(4, 16));
        var firstSession = Binding(6, 1);
        Assert.True(runtime.ActivateBinding(firstSession).IsSuccess);

        var baseline = await source.AcquireAsync(firstSession, 1);
        Assert.True(baseline.IsSuccess, baseline.Error?.Code.Value);
        Assert.True(runtime.Apply(baseline.Value).IsSuccess);

        connectionFactory.Available = false;
        var disconnected = await source.AcquireAsync(firstSession, 2);
        Assert.True(disconnected.IsFailure);
        var unavailable = source.CreateUnavailableCut(firstSession, 2);
        Assert.True(unavailable.IsSuccess, unavailable.Error?.Code.Value);
        Assert.True(runtime.Apply(unavailable.Value).IsSuccess);
        var stale = Assert.Single(runtime.GetSnapshot().Entries);
        Assert.Equal(DataQuality.Bad, stale.Quality);
        Assert.Equal(Freshness.Stale, stale.Freshness);

        connectionFactory.Available = true;
        var recoveredSession = Binding(6, 2);
        Assert.True(runtime.ActivateBinding(recoveredSession).IsSuccess);
        var recovered = await source.AcquireAsync(recoveredSession, 3);
        Assert.True(recovered.IsSuccess, recovered.Error?.Code.Value);
        Assert.True(runtime.Apply(recovered.Value).IsSuccess);
        var current = Assert.Single(runtime.GetSnapshot().Entries);
        Assert.Equal(SourceSessionGeneration.From(2), current.SessionGeneration);
        Assert.Equal(DataQuality.Good, current.Quality);
        Assert.Equal(Freshness.Fresh, current.Freshness);

        output.WriteLine(
            $"SNMP recovery value: {current.Value.Value} {current.Unit.Symbol}; " +
            $"session {current.SessionGeneration.Value}");
    }

    [PhysicalLabFact]
    [Trait("Category", "PhysicalLab")]
    public async Task WirenBoardReconnectRestoresGoodFreshCurrentWithNewSessionFence()
    {
        var host = Required("DISPATCHER_C15_MODBUS_HOST");
        var recoveryPort = OptionalInt("DISPATCHER_C15_MODBUS_PORT", 502);
        var unavailablePort = OptionalInt(
            "DISPATCHER_C15_MODBUS_UNAVAILABLE_PORT",
            502);
        var connectionFactory = new SwitchablePortModbusConnectionFactory(
            unavailablePort);
        using var source = CreateModbusSource(
            host,
            recoveryPort,
            unitId: 10,
            sourceNumber: 5,
            [
                ModbusPoint(
                    22,
                    ModbusRegisterTable.InputRegisters,
                    100,
                    ModbusValueType.Signed16,
                    "C") with { Scale = 0.1m },
            ],
            connectionFactory);
        var clock = new SystemClock(TimeProvider.System);
        var runtime = new CoreRuntime(
            ScopeId,
            clock,
            clock,
            new RuntimeCurrentLimits(4, 16));
        var firstSession = Binding(5, 1);
        Assert.True(runtime.ActivateBinding(firstSession).IsSuccess);

        var baseline = await source.AcquireAsync(
            new ProtocolSourceRequest(firstSession, 1, null));
        Assert.True(baseline.IsSuccess, baseline.Error?.Code.Value);
        Assert.True(runtime.Apply(baseline.Value).IsSuccess);
        Assert.Equal(DataQuality.Good, Assert.Single(runtime.GetSnapshot().Entries).Quality);

        connectionFactory.Available = false;
        var disconnected = await source.AcquireAsync(
            new ProtocolSourceRequest(firstSession, 2, null));
        Assert.True(disconnected.IsFailure);
        var unavailable = source.CreateUnavailableCut(firstSession, 2);
        Assert.True(unavailable.IsSuccess, unavailable.Error?.Code.Value);
        Assert.True(runtime.Apply(unavailable.Value).IsSuccess);
        var stale = Assert.Single(runtime.GetSnapshot().Entries);
        Assert.Equal(DataQuality.Bad, stale.Quality);
        Assert.Equal(Freshness.Stale, stale.Freshness);

        connectionFactory.Available = true;
        var recoveredSession = Binding(5, 2);
        Assert.True(runtime.ActivateBinding(recoveredSession).IsSuccess);
        var recovered = await source.AcquireAsync(
            new ProtocolSourceRequest(recoveredSession, 3, null));
        Assert.True(recovered.IsSuccess, recovered.Error?.Code.Value);
        Assert.True(runtime.Apply(recovered.Value).IsSuccess);
        var current = Assert.Single(runtime.GetSnapshot().Entries);
        Assert.Equal(SourceSessionGeneration.From(2), current.SessionGeneration);
        Assert.Equal(DataQuality.Good, current.Quality);
        Assert.Equal(Freshness.Fresh, current.Freshness);

        output.WriteLine(
            $"Modbus recovery value: {current.Value.Value} {current.Unit.Symbol}; " +
            $"session {current.SessionGeneration.Value}");
    }

    [PhysicalLabFact]
    [Trait("Category", "PhysicalLab")]
    public async Task WirenBoardUnavailableEndpointFailsWithinBoundedRetryWindow()
    {
        var host = Required("DISPATCHER_C15_MODBUS_HOST");
        var port = OptionalInt("DISPATCHER_C15_MODBUS_PORT", 502);
        using var source = CreateModbusSource(
            host,
            port,
            unitId: 10,
            sourceNumber: 4,
            [ModbusPoint(
                21,
                ModbusRegisterTable.InputRegisters,
                100,
                ModbusValueType.Signed16,
                "C") with { Scale = 0.1m }]);

        var timer = Stopwatch.StartNew();
        var result = await source.SamplePollAsync(Binding(4));
        timer.Stop();

        Assert.True(result.IsFailure);
        Assert.True(
            result.Error!.Code.Value is "protocol.io_failed" or "protocol.io_timeout",
            result.Error.Code.Value);
        Assert.True(
            timer.Elapsed < TimeSpan.FromSeconds(12),
            $"Bounded Modbus failure took {timer.Elapsed}.");

        output.WriteLine(
            $"Modbus unavailable evidence: {result.Error.Code.Value}; " +
            $"{timer.ElapsedMilliseconds} ms");
    }

    [PhysicalLabFact]
    [Trait("Category", "PhysicalLab")]
    public async Task WirenBoardReturnsConfiguredInputAndHoldingRegistersReadOnly()
    {
        var host = Required("DISPATCHER_C15_MODBUS_HOST");
        var port = OptionalInt("DISPATCHER_C15_MODBUS_PORT", 502);
        using var input = CreateModbusSource(
            host,
            port,
            unitId: 10,
            sourceNumber: 1,
            [
                ModbusPoint(1, ModbusRegisterTable.InputRegisters, 100, ModbusValueType.Signed16, "C")
                    with { Scale = 0.1m },
                ModbusPoint(2, ModbusRegisterTable.InputRegisters, 101, ModbusValueType.Unsigned16, "%")
                    with { Scale = 0.1m },
                ModbusPoint(3, ModbusRegisterTable.InputRegisters, 102, ModbusValueType.Signed32, "-"),
                ModbusPoint(4, ModbusRegisterTable.InputRegisters, 104, ModbusValueType.Unsigned32, "-")
                    with { WordOrder = ModbusWordOrder.LowWordFirst },
                ModbusPoint(5, ModbusRegisterTable.InputRegisters, 106, ModbusValueType.Unsigned16, "-")
                    with { ByteOrder = ModbusByteOrder.LittleEndian },
            ]);
        using var holding = CreateModbusSource(
            host,
            port,
            unitId: 12,
            sourceNumber: 2,
            [
                ModbusPoint(6, ModbusRegisterTable.HoldingRegisters, 200, ModbusValueType.Unsigned16, "-")
                    with { Scale = 0.1m },
            ]);

        var timer = Stopwatch.StartNew();
        var inputs = await input.SamplePollAsync(Binding(1));
        var setpoint = await holding.SamplePollAsync(Binding(2));
        timer.Stop();

        Assert.True(inputs.IsSuccess, inputs.Error?.Code.Value);
        Assert.True(setpoint.IsSuccess, setpoint.Error?.Code.Value);
        Assert.Equal(5, inputs.Value.Samples.Count);
        Assert.Single(setpoint.Value.Samples);
        Assert.All(inputs.Value.Samples.Concat(setpoint.Value.Samples), sample =>
        {
            Assert.Equal(DataQuality.Good, sample.Quality);
            Assert.Equal("modbus.ok", sample.Code);
        });

        output.WriteLine($"Modbus sample latency: {timer.ElapsedMilliseconds} ms");
        WriteSamples(inputs.Value.Samples.Concat(setpoint.Value.Samples));
    }

    [PhysicalLabFact]
    [Trait("Category", "PhysicalLab")]
    public async Task DeltaUpsReturnsConfiguredOidsWithEngineeringScaleReadOnly()
    {
        var host = Required("DISPATCHER_C15_SNMP_HOST");
        var port = OptionalInt("DISPATCHER_C15_SNMP_PORT", 161);
        var points = new[]
        {
            SnmpPoint(11, "1.3.6.1.4.1.2254.2.5.4.3.0", "V") with { Scale = 0.1m },
            SnmpPoint(12, "1.3.6.1.4.1.2254.2.5.5.4.0", "V") with { Scale = 0.1m },
            SnmpPoint(13, "1.3.6.1.4.1.2254.2.5.7.6.0", "V") with { Scale = 0.1m },
            SnmpPoint(14, "1.3.6.1.4.1.2254.2.5.7.10.0", "%"),
            SnmpPoint(15, "1.3.6.1.4.1.2254.2.5.9.32.0", "-"),
        };
        var configuration = new SnmpV2cSourceConfiguration(
            SnmpRuntimeProfile.ProductionV2cReadOnly,
            Source(3),
            host,
            port,
            ProtocolSecretReference.From("env:DISPATCHER_C15_SNMP_COMMUNITY"),
            points,
            new SnmpRetryPolicy(2, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100)));
        using var source = SnmpV2cSource.Create(
            configuration,
            new SnmpConfigurationLimits(16, 128, 512),
            new SnmpWireLimits(128, 4096),
            Workload,
            IoLimits,
            new EnvironmentLabSecretResolver(),
            new UdpSnmpDatagramClientFactory(),
            new SystemClock(TimeProvider.System)).Value;

        var timer = Stopwatch.StartNew();
        var result = await source.SamplePollAsync(Binding(3));
        timer.Stop();

        Assert.True(result.IsSuccess, result.Error?.Code.Value);
        Assert.Equal(points.Length, result.Value.Samples.Count);
        Assert.All(result.Value.Samples, sample =>
        {
            Assert.Equal(DataQuality.Good, sample.Quality);
            Assert.Equal("snmp.ok", sample.Code);
        });
        Assert.Equal(1m, result.Value.Samples[^1].Value);

        output.WriteLine($"SNMP sample latency: {timer.ElapsedMilliseconds} ms");
        WriteSamples(result.Value.Samples);
    }

    private void WriteSamples(IEnumerable<ProtocolDiagnosticSample> samples)
    {
        foreach (var sample in samples)
        {
            output.WriteLine(
                $"{sample.PointId.Value:D}: {sample.Value} {sample.Unit.Symbol}; " +
                $"{sample.Quality}; {sample.Code}");
        }
    }

    private static ModbusTcpSource CreateModbusSource(
        string host,
        int port,
        int unitId,
        int sourceNumber,
        IReadOnlyList<ModbusPointConfiguration> points,
        IModbusTcpConnectionFactory? connectionFactory = null)
    {
        var configuration = new ModbusTcpSourceConfiguration(
            ModbusRuntimeProfile.ProductionReadOnly,
            Source(sourceNumber),
            host,
            port,
            unitId,
            points,
            new ModbusRetryPolicy(2, TimeSpan.FromMilliseconds(100)));
        return ModbusTcpSource.Create(
            configuration,
            new ModbusConfigurationLimits(16, 32),
            Workload,
            IoLimits,
            connectionFactory ?? new TcpModbusConnectionFactory(),
            new SystemClock(TimeProvider.System)).Value;
    }

    private static ModbusPointConfiguration ModbusPoint(
        int number,
        ModbusRegisterTable table,
        int address,
        ModbusValueType type,
        string unit) =>
        new(
            Point(number),
            table,
            address,
            type,
            ModbusByteOrder.BigEndian,
            ModbusWordOrder.HighWordFirst,
            Unit.FromSymbol(unit));

    private static SnmpPointConfiguration SnmpPoint(
        int number,
        string oid,
        string unit) =>
        new(
            Point(number),
            SnmpOid.Parse(oid, 128, 512).Value,
            SnmpNumericType.Signed32,
            Unit.FromSymbol(unit));

    private static SnmpV2cSource CreateSnmpSource(
        string host,
        int port,
        int sourceNumber,
        IReadOnlyList<SnmpPointConfiguration> points,
        ISnmpDatagramClientFactory connectionFactory)
    {
        var configuration = new SnmpV2cSourceConfiguration(
            SnmpRuntimeProfile.ProductionV2cReadOnly,
            Source(sourceNumber),
            host,
            port,
            ProtocolSecretReference.From("env:DISPATCHER_C15_SNMP_COMMUNITY"),
            points,
            new SnmpRetryPolicy(
                2,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(100)));
        return SnmpV2cSource.Create(
            configuration,
            new SnmpConfigurationLimits(16, 128, 512),
            new SnmpWireLimits(128, 4096),
            Workload,
            IoLimits,
            new EnvironmentLabSecretResolver(),
            connectionFactory,
            new SystemClock(TimeProvider.System)).Value;
    }

    private static SourceBinding Binding(
        int sourceNumber,
        ulong sessionGeneration = 1) =>
        new(
            ScopeId,
            Source(sourceNumber),
            SourceBindingGeneration.From(1),
            SourceSessionGeneration.From(sessionGeneration));

    private static SourceId Source(int number) =>
        SourceId.From(Guid.Parse($"c1500000-0000-0000-0001-{number:D12}"));

    private static PointId Point(int number) =>
        PointId.From(Guid.Parse($"c1500000-0000-0000-0002-{number:D12}"));

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required physical lab setting {name} is absent.");

    private static int OptionalInt(string name, int defaultValue) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            ? value
            : defaultValue;

    private sealed class EnvironmentLabSecretResolver : IProtocolSecretResolver
    {
        public ValueTask<ProtocolSecretLease> ResolveAsync(
            ProtocolSecretReference reference,
            ProtocolWorkloadIdentity workloadIdentity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (workloadIdentity != Workload ||
                reference.Value != "env:DISPATCHER_C15_SNMP_COMMUNITY")
            {
                throw new UnauthorizedAccessException("Physical lab secret reference is invalid.");
            }

            return ValueTask.FromResult(
                ProtocolSecretLease.Create(Required("DISPATCHER_C15_SNMP_COMMUNITY")));
        }
    }

    private sealed class SwitchablePortModbusConnectionFactory(
        int unavailablePort) : IModbusTcpConnectionFactory
    {
        private readonly TcpModbusConnectionFactory inner = new();

        public bool Available { get; set; } = true;

        public ValueTask<IModbusTcpConnection> OpenAsync(
            string host,
            int port,
            CancellationToken cancellationToken) =>
            inner.OpenAsync(
                host,
                Available ? port : unavailablePort,
                cancellationToken);
    }

    private sealed class SwitchablePortSnmpClientFactory(
        int unavailablePort) : ISnmpDatagramClientFactory
    {
        private readonly UdpSnmpDatagramClientFactory inner = new();

        public bool Available { get; set; } = true;

        public ValueTask<ISnmpDatagramClient> OpenAsync(
            string host,
            int port,
            CancellationToken cancellationToken) =>
            inner.OpenAsync(
                host,
                Available ? port : unavailablePort,
                cancellationToken);
    }
}

internal sealed class PhysicalLabFactAttribute : FactAttribute
{
    public PhysicalLabFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DISPATCHER_C15_RUN"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set DISPATCHER_C15_RUN=1 to execute tests against physical devices.";
        }
    }
}
