using System.Text.Json;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Modbus;
using Dispatcher.Protocols;
using Dispatcher.Semantics;
using Dispatcher.Snmp;

namespace Dispatcher.ProtocolCommissioning;

public sealed record ProtocolCommissioningLimits(
    ModbusConfigurationLimits Modbus,
    SnmpConfigurationLimits Snmp);

public sealed record ProtocolActivationPlan(
    ConfigurationRevisionId RevisionId,
    RuntimeScopeId ScopeId,
    SourceBindingGeneration BindingGeneration,
    string ManifestFingerprint,
    IReadOnlyList<ModbusTcpSourceConfiguration> ModbusSources,
    IReadOnlyList<SnmpV2cSourceConfiguration> SnmpSources)
{
    public IReadOnlyList<SourceBinding> CreateBindings(SourceSessionGeneration sessionGeneration) =>
        ModbusSources.Select(source => source.SourceId)
            .Concat(SnmpSources.Select(source => source.SourceId))
            .OrderBy(sourceId => sourceId.Value)
            .Select(sourceId => new SourceBinding(
                ScopeId,
                sourceId,
                BindingGeneration,
                sessionGeneration))
            .ToArray();
}

public static class ProtocolCommissioningManifest
{
    public static Result<ProtocolActivationPlan> CreatePlan(
        ConfigurationRevisionSnapshot revision,
        ProtocolCommissioningLimits limits)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(limits);
        if (revision.PublishedAt is null || revision.DistributedAt is null)
        {
            return Failure("protocol.release_not_distributed", "Protocol release must be published and distributed.");
        }

        try
        {
            var normalized = ConfigurationManifestFingerprint.Normalize(revision.ManifestJson);
            if (!string.Equals(normalized.Fingerprint, revision.ManifestFingerprint, StringComparison.Ordinal))
            {
                return Failure("protocol.manifest_fingerprint", "Protocol manifest fingerprint does not match content.");
            }

            using var document = JsonDocument.Parse(normalized.Json);
            var sources = document.RootElement.GetProperty("protocolSources").EnumerateArray().ToArray();
            if (sources.Length == 0)
            {
                return Failure("protocol.manifest_empty", "Protocol manifest must contain at least one source.");
            }

            var modbus = new List<ModbusTcpSourceConfiguration>();
            var snmp = new List<SnmpV2cSourceConfiguration>();
            foreach (var source in sources)
            {
                var kind = source.GetProperty("kind").GetString();
                if (kind == "modbus_tcp_read_only")
                {
                    var config = ParseModbus(source);
                    var validation = config.Validate(limits.Modbus);
                    if (validation.IsFailure)
                    {
                        return Result.Failure<ProtocolActivationPlan>(validation.Error!);
                    }

                    modbus.Add(config);
                }
                else if (kind == "snmp_v2c_read_only")
                {
                    var config = ParseSnmp(source, limits.Snmp);
                    var validation = config.Validate(limits.Snmp);
                    if (validation.IsFailure)
                    {
                        return Result.Failure<ProtocolActivationPlan>(validation.Error!);
                    }

                    snmp.Add(config);
                }
                else
                {
                    return Failure("protocol.kind", "Protocol manifest contains an unsupported source kind.");
                }
            }

            var sourceIds = modbus.Select(item => item.SourceId).Concat(snmp.Select(item => item.SourceId)).ToArray();
            var pointIds = modbus.SelectMany(item => item.Points.Select(point => point.PointId))
                .Concat(snmp.SelectMany(item => item.Points.Select(point => point.PointId)))
                .ToArray();
            if (sourceIds.Distinct().Count() != sourceIds.Length ||
                pointIds.Distinct().Count() != pointIds.Length)
            {
                return Failure("protocol.manifest_identity", "Protocol source and point identities must be unique.");
            }

            return Result.Success(new ProtocolActivationPlan(
                revision.RevisionId,
                RuntimeScopeId.From(revision.ScopeId.Value),
                SourceBindingGeneration.From(revision.RevisionNumber.Value),
                revision.ManifestFingerprint,
                modbus,
                snmp));
        }
        catch (Exception exception) when (
            exception is JsonException or KeyNotFoundException or InvalidOperationException or
                ArgumentException or FormatException or OverflowException)
        {
            return Failure("protocol.manifest_invalid", "Protocol manifest structure is invalid.");
        }
    }

    private static ModbusTcpSourceConfiguration ParseModbus(JsonElement source)
    {
        var retry = source.GetProperty("retry");
        return new ModbusTcpSourceConfiguration(
            ModbusRuntimeProfile.NonProductionReadOnly,
            SourceId.From(source.GetProperty("sourceId").GetGuid()),
            source.GetProperty("host").GetString()!,
            source.GetProperty("port").GetInt32(),
            source.GetProperty("unitId").GetInt32(),
            source.GetProperty("points").EnumerateArray().Select(point => new ModbusPointConfiguration(
                PointId.From(point.GetProperty("pointId").GetGuid()),
                point.GetProperty("table").GetString() switch
                {
                    "holding" => ModbusRegisterTable.HoldingRegisters,
                    "input" => ModbusRegisterTable.InputRegisters,
                    _ => throw new FormatException("Unsupported Modbus table."),
                },
                point.GetProperty("address").GetInt32(),
                point.GetProperty("type").GetString() switch
                {
                    "signed16" => ModbusValueType.Signed16,
                    "unsigned16" => ModbusValueType.Unsigned16,
                    "signed32" => ModbusValueType.Signed32,
                    "unsigned32" => ModbusValueType.Unsigned32,
                    _ => throw new FormatException("Unsupported Modbus type."),
                },
                point.GetProperty("byteOrder").GetString() == "big"
                    ? ModbusByteOrder.BigEndian
                    : point.GetProperty("byteOrder").GetString() == "little"
                        ? ModbusByteOrder.LittleEndian
                        : throw new FormatException("Unsupported Modbus byte order."),
                point.GetProperty("wordOrder").GetString() == "high_first"
                    ? ModbusWordOrder.HighWordFirst
                    : point.GetProperty("wordOrder").GetString() == "low_first"
                        ? ModbusWordOrder.LowWordFirst
                        : throw new FormatException("Unsupported Modbus word order."),
                Unit.FromSymbol(point.GetProperty("unit").GetString()!))).ToArray(),
            new ModbusRetryPolicy(
                retry.GetProperty("maxAttempts").GetInt32(),
                TimeSpan.FromMilliseconds(retry.GetProperty("delayMs").GetInt32())));
    }

    private static SnmpV2cSourceConfiguration ParseSnmp(
        JsonElement source,
        SnmpConfigurationLimits limits)
    {
        var retry = source.GetProperty("retry");
        return new SnmpV2cSourceConfiguration(
            SnmpRuntimeProfile.NonProductionV2cReadOnly,
            SourceId.From(source.GetProperty("sourceId").GetGuid()),
            source.GetProperty("host").GetString()!,
            source.GetProperty("port").GetInt32(),
            ProtocolSecretReference.From(source.GetProperty("communityReference").GetString()!),
            source.GetProperty("points").EnumerateArray().Select(point => new SnmpPointConfiguration(
                PointId.From(point.GetProperty("pointId").GetGuid()),
                SnmpOid.Parse(
                    point.GetProperty("oid").GetString()!,
                    limits.MaxOidArcs,
                    limits.MaxOidBytes).Value,
                point.GetProperty("type").GetString() switch
                {
                    "signed32" => SnmpNumericType.Signed32,
                    "counter32" => SnmpNumericType.Counter32,
                    "gauge32" => SnmpNumericType.Gauge32,
                    "timeticks" => SnmpNumericType.TimeTicks,
                    "counter64" => SnmpNumericType.Counter64,
                    _ => throw new FormatException("Unsupported SNMP type."),
                },
                Unit.FromSymbol(point.GetProperty("unit").GetString()!))).ToArray(),
            new SnmpRetryPolicy(
                retry.GetProperty("maxAttempts").GetInt32(),
                TimeSpan.FromMilliseconds(retry.GetProperty("responseTimeoutMs").GetInt32()),
                TimeSpan.FromMilliseconds(retry.GetProperty("delayMs").GetInt32())));
    }

    private static Result<ProtocolActivationPlan> Failure(string code, string message) =>
        Result.Failure<ProtocolActivationPlan>(new OperationError(ErrorCode.From(code), message));
}
