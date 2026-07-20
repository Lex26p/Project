using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Dispatcher.Alarm;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.History;
using Dispatcher.Modbus;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.ProtocolCommissioning;
using Dispatcher.Protocols;
using Dispatcher.Semantics;
using Dispatcher.Server;
using Dispatcher.Snmp;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class ProtocolCommissioningAcceptanceTests
{
    private const string Community = "commissioning-community";
    private static readonly DateTimeOffset Start = new(2026, 7, 20, 15, 0, 0, TimeSpan.Zero);
    private static readonly FacilityScopeId FacilityScope = FacilityScopeId.From(
        Guid.Parse("e1000000-0000-0000-0000-000000000001"));
    private static readonly RuntimeScopeId RuntimeScope = RuntimeScopeId.From(FacilityScope.Value);
    private static readonly SourceId ModbusSourceId = SourceId.From(
        Guid.Parse("e2000000-0000-0000-0000-000000000001"));
    private static readonly SourceId SnmpSourceId = SourceId.From(
        Guid.Parse("e2000000-0000-0000-0000-000000000002"));
    private static readonly PointId ModbusPointId = PointId.From(
        Guid.Parse("e3000000-0000-0000-0000-000000000001"));
    private static readonly PointId SnmpPointId = PointId.From(
        Guid.Parse("e3000000-0000-0000-0000-000000000002"));
    private static readonly SnmpOid SnmpOid = Dispatcher.Snmp.SnmpOid.Parse(
        "1.3.6.1.2.1.1.3.0",
        32,
        128).Value;
    private static readonly ProtocolCommissioningLimits CommissioningLimits = new(
        new ModbusConfigurationLimits(16, 32),
        new SnmpConfigurationLimits(16, 32, 128));
    private static readonly ProtocolIoLimits IoLimits = new(TimeSpan.FromSeconds(1), 4096, 16, 1);
    private static readonly ProtocolWorkloadIdentity Workload =
        ProtocolWorkloadIdentity.From("dispatcher-runtime:windows-x64");
    private readonly PostgreSqlClusterFixture cluster;

    public ProtocolCommissioningAcceptanceTests(PostgreSqlClusterFixture cluster) => this.cluster = cluster;

    [Fact]
    public async Task BothAdaptersPassStagingReleaseCurrentHistoryAlarmAndWebProjectionAfterReconnects()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            ConfigurationMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            HistoryMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            AlarmMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
        var clock = new FixedClock();
        var session = Session();

        Assert.Empty(EquipmentStagingTools.Validate(StagingRow(EquipmentProtocol.ModbusTcp)));
        Assert.Empty(EquipmentStagingTools.Validate(StagingRow(EquipmentProtocol.Snmp)));

        var configuration = new ConfigurationService(
            new ConfigurationStore(dataSource, PostgreSqlClusterFixture.OwnerARole, clock),
            clock);
        var draft = (await configuration.SaveAsync(
            session,
            FacilityScope,
            new SaveConfigurationRequest(Manifest(), [], null))).Value;
        var validated = (await configuration.ValidateAsync(
            session,
            FacilityScope,
            draft.RevisionId,
            draft.Version)).Value;
        var published = (await configuration.PublishAsync(
            session,
            FacilityScope,
            new PublishConfigurationRequest(validated.RevisionId, validated.Version, []))).Value;
        var job = (await configuration.ClaimDistributionAsync(
            session,
            FacilityScope,
            "protocol-commissioning",
            TimeSpan.FromMinutes(1))).Value;
        var distributed = (await configuration.CompleteDistributionAsync(
            session,
            FacilityScope,
            job.JobId,
            "protocol-commissioning")).Value;
        var plan = ProtocolCommissioningManifest.CreatePlan(distributed, CommissioningLimits).Value;
        var activated = await configuration.AcknowledgeActivationAsync(
            session,
            FacilityScope,
            distributed.RevisionId,
            distributed.Version);
        Assert.True(activated.IsSuccess);

        var bindingBySource = plan.CreateBindings(SourceSessionGeneration.From(1))
            .ToDictionary(binding => binding.SourceId);
        var core = new CoreRuntime(RuntimeScope, clock, clock, new RuntimeCurrentLimits(16, 64));
        Assert.All(bindingBySource.Values, binding => Assert.True(core.ActivateBinding(binding).IsSuccess));
        var history = new HistoryStore(
            dataSource,
            PostgreSqlClusterFixture.OwnerARole,
            clock,
            new HistoryQueryLimits(100, 100));
        var alarmStore = new AlarmStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        Assert.True((await alarmStore.ActivateDefinitionSetAsync(new AlarmDefinitionSet(
            RuntimeScope,
            distributed.RevisionNumber,
            [
                Alarm(ModbusPointId, "Modbus high"),
                Alarm(SnmpPointId, "SNMP high"),
            ]))).IsSuccess);
        var alarm = new AlarmEvaluator(RuntimeScope, distributed.RevisionNumber, alarmStore);

        var modbusFactory = new ReconnectingModbusFactory(
            new IOException("simulated reconnect"),
            new StaticModbusConnection(ModbusResponse(1, 1, 3, 0, 42)));
        using var modbus = ModbusTcpSource.Create(
            plan.ModbusSources[0],
            CommissioningLimits.Modbus,
            Workload,
            IoLimits,
            modbusFactory,
            clock).Value;
        var snmpFactory = new ReconnectingSnmpFactory(
            new IOException("simulated reconnect"),
            new StaticSnmpClient(SnmpResponse(
                1,
                Community,
                SnmpVariable(SnmpOid, 0x43, [50]))));
        using var snmp = SnmpV2cSource.Create(
            plan.SnmpSources[0],
            CommissioningLimits.Snmp,
            new SnmpWireLimits(128, 4096),
            Workload,
            IoLimits,
            new StaticSecretResolver(),
            snmpFactory,
            clock).Value;

        var modbusAcquire = modbus.AcquireAsync(new ProtocolSourceRequest(
            bindingBySource[ModbusSourceId],
            1,
            null));
        var snmpAcquire = snmp.AcquireAsync(bindingBySource[SnmpSourceId], 1);
        await Task.WhenAll(modbusAcquire, snmpAcquire);
        var modbusCut = (await modbusAcquire).Value;
        var snmpCut = (await snmpAcquire).Value;
        var modbusAcceptance = core.Apply(modbusCut).Value;
        var snmpAcceptance = core.Apply(snmpCut).Value;
        Assert.True((await history.AcceptAsync(Obligation(1, modbusCut))).IsSuccess);
        Assert.True((await history.AcceptAsync(Obligation(2, snmpCut))).IsSuccess);
        Assert.True((await alarm.EvaluatePostRuntimeCutAsync(modbusAcceptance, core.GetSnapshot())).IsSuccess);
        Assert.True((await alarm.EvaluatePostRuntimeCutAsync(snmpAcceptance, core.GetSnapshot())).IsSuccess);

        var registry = new RuntimeRegistry();
        registry.Add(RuntimeScope, core);
        var web = new AuthorizedRuntimeReader(registry, clock).ReadSnapshot(session, RuntimeScope);

        Assert.True(web.IsSuccess);
        Assert.Equal(2, web.Value.Payload.Points.Count);
        Assert.Equal(2, modbusFactory.OpenAttempts);
        Assert.Equal(2, snmpFactory.OpenAttempts);
        Assert.Equal([42L, 50L], web.Value.Payload.Points.OrderBy(point => point.PointId).Select(point => point.Value));
    }

    private static RuntimeSourceObligation Obligation(ulong position, RuntimeCut cut) => new(
        RuntimeScope,
        new OwnerPosition<RuntimeSourceObligation>(position),
        RuntimeFactClass.SourceCut,
        RuntimeFactProtection.Protected,
        cut.Binding,
        cut.ScheduleSequence,
        cut,
        null,
        Start,
        Start);

    private static AlarmDefinition Alarm(PointId pointId, string name) => new(
        AlarmDefinitionId.New(),
        pointId,
        name,
        AlarmThresholdDirection.High,
        10,
        0,
        TimeSpan.Zero,
        TimeSpan.Zero);

    private static StagingRowInput StagingRow(EquipmentProtocol protocol) => new(
        Guid.NewGuid(),
        EquipmentId.From(Guid.NewGuid()),
        FacilityScope,
        LocationId.From(Guid.NewGuid()),
        protocol == EquipmentProtocol.ModbusTcp ? "PLC-1" : "SNMP-1",
        protocol == EquipmentProtocol.ModbusTcp ? "PLC" : "SNMP device",
        protocol == EquipmentProtocol.ModbusTcp
            ? EquipmentProtocolForm.NewModbusTcp() with { Host = "127.0.0.1" }
            : EquipmentProtocolForm.NewSnmp() with
            {
                Host = "127.0.0.1",
                Secret = WriteOnlySecret.From(Community),
            });

    private static SessionSnapshot Session()
    {
        var permissions = new List<PermissionCode>
        {
            ConfigurationPermissions.Read(FacilityScope),
            ConfigurationPermissions.Save(FacilityScope),
            ConfigurationPermissions.Validate(FacilityScope),
            ConfigurationPermissions.Publish(FacilityScope),
            ConfigurationPermissions.Distribute(FacilityScope),
            ConfigurationPermissions.Activate(FacilityScope),
            RuntimePermissions.ReadCurrent,
            RuntimePermissions.ReadPoint(ModbusPointId),
            RuntimePermissions.ReadPoint(SnmpPointId),
        };
        return new SessionSnapshot(
            SessionId.New(),
            SubjectId.New(),
            PrincipalKind.User,
            Start.AddMinutes(-1),
            Start.AddHours(1),
            new EffectivePermissions(permissions));
    }

    private static string Manifest() => JsonSerializer.Serialize(new
    {
        protocolSources = new object[]
        {
            new
            {
                kind = "modbus_tcp_read_only",
                sourceId = ModbusSourceId.Value,
                host = "127.0.0.1",
                port = 502,
                unitId = 1,
                retry = new { maxAttempts = 2, delayMs = 0 },
                points = new[]
                {
                    new
                    {
                        pointId = ModbusPointId.Value,
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
                sourceId = SnmpSourceId.Value,
                host = "127.0.0.1",
                port = 161,
                communityReference = "env:SNMP_COMMUNITY",
                retry = new { maxAttempts = 2, responseTimeoutMs = 100, delayMs = 0 },
                points = new[]
                {
                    new
                    {
                        pointId = SnmpPointId.Value,
                        oid = SnmpOid.Value,
                        type = "timeticks",
                        unit = "s",
                    },
                },
            },
        },
    });

    private static byte[] ModbusResponse(ushort transactionId, byte unitId, byte function, params byte[] data)
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

    private static byte[] SnmpResponse(int requestId, string community, params byte[][] variables) =>
        Tlv(0x30, Concat(
            Tlv(0x02, [1]),
            Tlv(0x04, Encoding.UTF8.GetBytes(community)),
            Tlv(0xA2, Concat(
                Tlv(0x02, PositiveInteger(requestId)),
                Tlv(0x02, [0]),
                Tlv(0x02, [0]),
                Tlv(0x30, Concat(variables))))));

    private static byte[] SnmpVariable(SnmpOid oid, byte tag, byte[] value) =>
        Tlv(0x30, Concat(Tlv(0x06, EncodeOid(oid)), Tlv(tag, value)));

    private static byte[] EncodeOid(SnmpOid oid)
    {
        var arcs = oid.Value.Split('.').Select(uint.Parse).ToArray();
        var bytes = new List<byte>();
        AppendBase128(bytes, checked((ulong)(arcs[0] * 40) + arcs[1]));
        foreach (var arc in arcs.Skip(2))
        {
            AppendBase128(bytes, arc);
        }

        return bytes.ToArray();
    }

    private static void AppendBase128(List<byte> output, ulong value)
    {
        Span<byte> buffer = stackalloc byte[10];
        var offset = buffer.Length;
        buffer[--offset] = checked((byte)(value & 0x7F));
        while ((value >>= 7) != 0)
        {
            buffer[--offset] = checked((byte)((value & 0x7F) | 0x80));
        }

        output.AddRange(buffer[offset..].ToArray());
    }

    private static byte[] PositiveInteger(int value)
    {
        var bytes = new List<byte>();
        do
        {
            bytes.Insert(0, checked((byte)value));
            value >>= 8;
        }
        while (value > 0);
        if (bytes[0] >= 0x80)
        {
            bytes.Insert(0, 0);
        }

        return bytes.ToArray();
    }

    private static byte[] Tlv(byte tag, byte[] value) =>
        value.Length < 0x80
            ? [tag, checked((byte)value.Length), .. value]
            : [tag, 0x82, checked((byte)(value.Length >> 8)), checked((byte)value.Length), .. value];

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private sealed class FixedClock : IWallClock, IMonotonicClock
    {
        public DateTimeOffset GetUtcNow() => Start;
        public MonotonicTimestamp GetTimestamp() => new(0);
        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) => TimeSpan.Zero;
    }

    private sealed class StaticSecretResolver : IProtocolSecretResolver
    {
        public ValueTask<ProtocolSecretLease> ResolveAsync(
            ProtocolSecretReference reference,
            ProtocolWorkloadIdentity workloadIdentity,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ProtocolSecretLease.Create(Community));
    }

    private sealed class ReconnectingModbusFactory : IModbusTcpConnectionFactory
    {
        private readonly Queue<object> outcomes;
        public ReconnectingModbusFactory(params object[] outcomes) => this.outcomes = new(outcomes);
        public int OpenAttempts { get; private set; }

        public ValueTask<IModbusTcpConnection> OpenAsync(string host, int port, CancellationToken cancellationToken)
        {
            OpenAttempts++;
            var outcome = outcomes.Dequeue();
            return outcome is Exception exception
                ? ValueTask.FromException<IModbusTcpConnection>(exception)
                : ValueTask.FromResult((IModbusTcpConnection)outcome);
        }
    }

    private sealed class StaticModbusConnection : IModbusTcpConnection
    {
        private readonly ReadOnlyMemory<byte> response;
        public StaticModbusConnection(ReadOnlyMemory<byte> response) => this.response = response;
        public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> request,
            int maxResponseBytes,
            CancellationToken cancellationToken) => ValueTask.FromResult(response);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReconnectingSnmpFactory : ISnmpDatagramClientFactory
    {
        private readonly Queue<object> outcomes;
        public ReconnectingSnmpFactory(params object[] outcomes) => this.outcomes = new(outcomes);
        public int OpenAttempts { get; private set; }

        public ValueTask<ISnmpDatagramClient> OpenAsync(string host, int port, CancellationToken cancellationToken)
        {
            OpenAttempts++;
            var outcome = outcomes.Dequeue();
            return outcome is Exception exception
                ? ValueTask.FromException<ISnmpDatagramClient>(exception)
                : ValueTask.FromResult((ISnmpDatagramClient)outcome);
        }
    }

    private sealed class StaticSnmpClient : ISnmpDatagramClient
    {
        private readonly ReadOnlyMemory<byte> response;
        public StaticSnmpClient(ReadOnlyMemory<byte> response) => this.response = response;
        public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> request,
            int maxResponseBytes,
            CancellationToken cancellationToken) => ValueTask.FromResult(response);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
