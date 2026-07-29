using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dispatcher.Equipment;

public static class EquipmentCommissioningTools
{
    public static IReadOnlyList<StagingFieldError> ValidateDraft(
        EquipmentStagingDraftInput input,
        bool hasSecret)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new List<StagingFieldError>();
        if (input.RowId == Guid.Empty)
        {
            errors.Add(Error("row_id", "staging.required", "Row ID is required."));
        }

        if (string.IsNullOrWhiteSpace(input.Code))
        {
            errors.Add(Error("code", "staging.required", "Equipment code is required."));
        }

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            errors.Add(Error("name", "staging.required", "Equipment name is required."));
        }

        if (string.IsNullOrWhiteSpace(input.Host))
        {
            errors.Add(Error("host", "staging.required", "Host is required."));
        }

        if (input.Port is < 1 or > 65535)
        {
            errors.Add(Error("port", "staging.range", "Port must be between 1 and 65535."));
        }

        if (string.IsNullOrWhiteSpace(input.Unit) || input.Unit.Length > 32)
        {
            errors.Add(Error("unit", "staging.value", "A bounded unit is required."));
        }

        if (input.Protocol == EquipmentProtocol.ModbusTcp)
        {
            if (input.ModbusUnitId is < 0 or > 255)
            {
                errors.Add(Error("modbus_unit_id", "staging.range", "Modbus Unit ID must be between 0 and 255."));
            }

            if (input.ModbusTable is not ("holding" or "input"))
            {
                errors.Add(Error("modbus_table", "staging.value", "Modbus table must be holding or input."));
            }

            if (input.ModbusAddress is < 0 or > 65535)
            {
                errors.Add(Error("modbus_address", "staging.range", "Modbus address must be between 0 and 65535."));
            }

            if (input.ModbusValueType is not ("signed16" or "unsigned16" or "signed32" or "unsigned32"))
            {
                errors.Add(Error("modbus_value_type", "staging.value", "Modbus value type is unsupported."));
            }

            if (input.ModbusByteOrder is not ("big" or "little") ||
                input.ModbusWordOrder is not ("high_first" or "low_first"))
            {
                errors.Add(Error("modbus_order", "staging.value", "Modbus byte or word order is unsupported."));
            }

            if (input.ModbusScale is null or 0m)
            {
                errors.Add(Error("modbus_scale", "staging.value", "Modbus scale must be non-zero."));
            }
        }
        else
        {
            if (input.SnmpVersion != "v2c")
            {
                errors.Add(Error("snmp_version", "staging.value", "Only SNMP v2c is supported."));
            }

            if (!hasSecret)
            {
                errors.Add(Error("secret", "staging.required", "SNMP community is required."));
            }

            if (string.IsNullOrWhiteSpace(input.SnmpOid))
            {
                errors.Add(Error("snmp_oid", "staging.required", "SNMP OID is required."));
            }

            if (input.SnmpValueType is not ("signed32" or "counter32" or "gauge32" or "timeticks" or "counter64"))
            {
                errors.Add(Error("snmp_value_type", "staging.value", "SNMP value type is unsupported."));
            }
        }

        if (input.Action == StagingApplyAction.Update && input.EquipmentId == default)
        {
            errors.Add(Error("equipment_id", "staging.required", "An existing equipment ID is required for update."));
        }

        return errors;
    }

    internal static string ComputeFingerprint(
        EquipmentStagingDraftInput input,
        string? secretReference)
    {
        var payload = JsonSerializer.Serialize(new
        {
            input.RowId,
            EquipmentId = input.EquipmentId.Value,
            ScopeId = input.ScopeId.Value,
            LocationId = input.LocationId.Value,
            input.Code,
            input.Name,
            input.Protocol,
            input.Host,
            input.Port,
            input.ModbusUnitId,
            input.ModbusTable,
            input.ModbusAddress,
            input.ModbusValueType,
            input.ModbusByteOrder,
            input.ModbusWordOrder,
            input.ModbusScale,
            input.SnmpVersion,
            input.SnmpOid,
            input.SnmpValueType,
            input.Unit,
            SecretReference = secretReference,
            input.Action,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    internal static string BuildDiagnosticManifest(
        EquipmentStagingDraftSnapshot draft,
        string? secretReference)
    {
        object source = draft.Protocol == EquipmentProtocol.ModbusTcp
            ? new
            {
                kind = "modbus_tcp_read_only",
                sourceId = draft.EquipmentId.Value,
                host = draft.Host,
                port = draft.Port,
                unitId = draft.ModbusUnitId,
                retry = new { maxAttempts = 2, delayMs = 50 },
                points = new[]
                {
                    new
                    {
                        pointId = draft.RowId,
                        table = draft.ModbusTable,
                        address = draft.ModbusAddress,
                        type = draft.ModbusValueType,
                        byteOrder = draft.ModbusByteOrder,
                        wordOrder = draft.ModbusWordOrder,
                        scale = draft.ModbusScale,
                        unit = draft.Unit,
                    },
                },
            }
            : new
            {
                kind = "snmp_v2c_read_only",
                sourceId = draft.EquipmentId.Value,
                host = draft.Host,
                port = draft.Port,
                communityReference = secretReference,
                retry = new { maxAttempts = 2, responseTimeoutMs = 1000, delayMs = 50 },
                points = new[]
                {
                    new
                    {
                        pointId = draft.RowId,
                        oid = draft.SnmpOid,
                        type = draft.SnmpValueType,
                        unit = draft.Unit,
                    },
                },
            };
        return JsonSerializer.Serialize(new { protocolSources = new[] { source } });
    }

    private static StagingFieldError Error(string field, string code, string message) =>
        new(field, code, message);
}
