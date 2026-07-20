using System.Text.Json;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Facilities;
using Dispatcher.Modbus;
using Dispatcher.ProtocolCommissioning;
using Dispatcher.Semantics;
using Dispatcher.Snmp;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class ProtocolCommissioningTests
{
    private static readonly Guid Scope = Guid.Parse("d1000000-0000-0000-0000-000000000001");
    private static readonly Guid ModbusSource = Guid.Parse("d2000000-0000-0000-0000-000000000001");
    private static readonly Guid SnmpSource = Guid.Parse("d2000000-0000-0000-0000-000000000002");
    private static readonly Guid ModbusPoint = Guid.Parse("d3000000-0000-0000-0000-000000000001");
    private static readonly Guid SnmpPoint = Guid.Parse("d3000000-0000-0000-0000-000000000002");
    private static readonly ProtocolCommissioningLimits Limits = new(
        new ModbusConfigurationLimits(32, 64),
        new SnmpConfigurationLimits(32, 32, 128));

    [Fact]
    public void DistributedRevisionCreatesOneFrozenPlanForBothReadOnlyAdapters()
    {
        var plan = ProtocolCommissioningManifest.CreatePlan(Revision(), Limits);

        Assert.True(plan.IsSuccess);
        Assert.Single(plan.Value.ModbusSources);
        Assert.Single(plan.Value.SnmpSources);
        Assert.Equal((ulong)7, plan.Value.BindingGeneration.Value);
        var bindings = plan.Value.CreateBindings(SourceSessionGeneration.From(3));
        Assert.Equal(2, bindings.Count);
        Assert.All(bindings, binding => Assert.Equal((ulong)3, binding.SessionGeneration.Value));
    }

    [Fact]
    public void ProcessFailureDegradesOnlyScopeAndRecoveryFencesPreviousSession()
    {
        var plan = ProtocolCommissioningManifest.CreatePlan(Revision(), Limits).Value;
        var continuity = new ProtocolDeploymentContinuity(plan);
        var firstSession = SourceSessionGeneration.From(1);
        var secondSession = SourceSessionGeneration.From(2);

        var first = continuity.Activate(firstSession);
        Assert.True(first.IsSuccess);
        Assert.True(continuity.MarkProcessUnavailable(firstSession).IsSuccess);
        Assert.Equal(
            ProtocolDeploymentState.DegradedProcessUnavailable,
            continuity.GetSnapshot().State);

        var recovered = continuity.Activate(secondSession);

        Assert.True(recovered.IsSuccess);
        Assert.Equal(ProtocolDeploymentState.Active, continuity.GetSnapshot().State);
        Assert.All(recovered.Value, binding => Assert.Equal(secondSession, binding.SessionGeneration));
        var clock = new FixedClock();
        var core = new CoreRuntime(plan.ScopeId, clock, clock, new RuntimeCurrentLimits(8, 16));
        Assert.All(first.Value, binding => Assert.True(core.ActivateBinding(binding).IsSuccess));
        Assert.All(recovered.Value, binding => Assert.True(core.ActivateBinding(binding).IsSuccess));
        var previousBinding = first.Value.Single(binding => binding.SourceId.Value == ModbusSource);
        var lateObservation = new SourceObservation(
            plan.ScopeId,
            previousBinding.SourceId,
            PointId.From(ModbusPoint),
            new OwnerPosition<SourceObservation>(1),
            TypedValue.From(42L),
            Unit.FromSymbol("kW"),
            DataQuality.Good,
            Freshness.Fresh,
            SourceTimestamp.FromUtc(FixedClock.Now));
        var lateCut = RuntimeCut.Normalize(previousBinding, 1, [lateObservation]).Value;
        Assert.Equal("core.binding_stale", core.Apply(lateCut).Error?.Code.Value);
        Assert.Equal(
            "protocol.session_stale",
            continuity.Activate(firstSession).Error?.Code.Value);
    }

    [Fact]
    public void ProtocolsHaveSeparateWindowsQualificationAndDg07RemainsOpenWithoutLinuxEvidence()
    {
        var evidence = new ProtocolQualificationEvidence(
            EndToEndConsumers: true,
            MultiDeviceLoad: true,
            ReconnectStorm: true,
            ProcessCrashRecovery: true,
            ReadOnlySurfaceFrozen: true,
            WindowsX64: true,
            LinuxX64: false);

        var modbus = ProtocolQualificationRecord.Create(QualifiedProtocolProfile.ModbusTcpReadOnly, evidence);
        var snmp = ProtocolQualificationRecord.Create(QualifiedProtocolProfile.SnmpV2cReadOnly, evidence);

        Assert.True(modbus.IsSuccess);
        Assert.True(snmp.IsSuccess);
        Assert.Equal(ProtocolQualificationStatus.WindowsQualifiedPlatformPending, modbus.Value.Status);
        Assert.Equal(ProtocolQualificationStatus.WindowsQualifiedPlatformPending, snmp.Value.Status);
        Assert.False(modbus.Value.ClosesDg07);
        Assert.False(snmp.Value.ClosesDg07);
        Assert.NotEqual(modbus.Value.ContractFingerprint, snmp.Value.ContractFingerprint);
    }

    private static ConfigurationRevisionSnapshot Revision()
    {
        var manifest = JsonSerializer.Serialize(new
        {
            protocolSources = new object[]
            {
                new
                {
                    kind = "modbus_tcp_read_only",
                    sourceId = ModbusSource,
                    host = "127.0.0.1",
                    port = 502,
                    unitId = 1,
                    retry = new { maxAttempts = 2, delayMs = 0 },
                    points = new[]
                    {
                        new
                        {
                            pointId = ModbusPoint,
                            table = "holding",
                            address = 10,
                            type = "unsigned16",
                            byteOrder = "big",
                            wordOrder = "high_first",
                            unit = "kW",
                        },
                    },
                },
                new
                {
                    kind = "snmp_v2c_read_only",
                    sourceId = SnmpSource,
                    host = "127.0.0.1",
                    port = 161,
                    communityReference = "env:SNMP_COMMUNITY",
                    retry = new { maxAttempts = 2, responseTimeoutMs = 100, delayMs = 0 },
                    points = new[]
                    {
                        new
                        {
                            pointId = SnmpPoint,
                            oid = "1.3.6.1.2.1.1.3.0",
                            type = "timeticks",
                            unit = "s",
                        },
                    },
                },
            },
        });
        var normalized = ConfigurationManifestFingerprint.Normalize(manifest);
        var now = new DateTimeOffset(2026, 7, 20, 14, 0, 0, TimeSpan.Zero);
        return new ConfigurationRevisionSnapshot(
            ConfigurationRevisionId.New(),
            FacilityScopeId.From(Scope),
            RevisionNumber.From(7),
            null,
            normalized.Json,
            normalized.Fingerprint,
            [],
            "dependencies",
            1,
            now,
            now,
            now,
            now,
            now);
    }

    private sealed class FixedClock : IWallClock, IMonotonicClock
    {
        public static DateTimeOffset Now { get; } =
            new(2026, 7, 20, 14, 0, 0, TimeSpan.Zero);

        public DateTimeOffset GetUtcNow() => Now;
        public MonotonicTimestamp GetTimestamp() => new(0);
        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) => TimeSpan.Zero;
    }
}
