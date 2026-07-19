using System.Globalization;
using System.Text;
using Dispatcher.Facilities;

namespace Dispatcher.Equipment;

public static class EquipmentStagingTools
{
    private const int MaximumCsvCharacters = 1_000_000;
    private const int MaximumCsvRecords = 10_000;
    private const int MaximumFieldCharacters = 4_096;
    private static readonly string[] Headers =
    [
        "row_id", "equipment_id", "scope_id", "location_id", "code", "name", "protocol",
        "host", "port", "modbus_unit_id", "snmp_version", "secret",
    ];

    public static IReadOnlyList<StagingFieldError> Validate(StagingRowInput row)
    {
        var errors = new List<StagingFieldError>();
        if (row.RowId == Guid.Empty)
        {
            errors.Add(Error("row_id", "staging.required", "Row ID is required."));
        }

        if (string.IsNullOrWhiteSpace(row.Code))
        {
            errors.Add(Error("code", "staging.required", "Equipment code is required."));
        }

        if (string.IsNullOrWhiteSpace(row.Name))
        {
            errors.Add(Error("name", "staging.required", "Equipment name is required."));
        }

        if (string.IsNullOrWhiteSpace(row.Form.Host))
        {
            errors.Add(Error("host", "staging.required", "Host is required."));
        }

        if (row.Form.Port is < 1 or > 65535)
        {
            errors.Add(Error("port", "staging.range", "Port must be between 1 and 65535."));
        }

        if (row.Form.Protocol == EquipmentProtocol.ModbusTcp)
        {
            if (row.Form.ModbusUnitId is < 0 or > 255)
            {
                errors.Add(Error("modbus_unit_id", "staging.range", "Modbus Unit ID must be between 0 and 255."));
            }

            if (row.Form.SnmpVersion is not null || row.Form.Secret is not null)
            {
                errors.Add(Error("protocol", "staging.protocol_fields", "SNMP fields are not valid for Modbus TCP."));
            }
        }
        else
        {
            if (row.Form.ModbusUnitId is not null)
            {
                errors.Add(Error("modbus_unit_id", "staging.protocol_fields", "Modbus Unit ID is not valid for SNMP."));
            }

            if (row.Form.SnmpVersion is not ("v1" or "v2c"))
            {
                errors.Add(Error("snmp_version", "staging.value", "SNMP version must be v1 or v2c."));
            }

            if (row.Form.Secret is null)
            {
                errors.Add(Error("secret", "staging.required", "SNMP community is required."));
            }
        }

        return errors;
    }

    public static IReadOnlyList<StagingRowInput> Copy(
        StagingRowInput source,
        int quantity,
        bool incrementModbusUnitId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);
        var rows = new List<StagingRowInput>(quantity);
        for (var index = 1; index <= quantity; index++)
        {
            var unitId = source.Form.ModbusUnitId;
            if (incrementModbusUnitId)
            {
                if (source.Form.Protocol != EquipmentProtocol.ModbusTcp || unitId is null || unitId + index > 255)
                {
                    throw new ArgumentException("Modbus Unit ID increment is not valid for this copy request.");
                }

                unitId += index;
            }

            rows.Add(source with
            {
                RowId = Guid.CreateVersion7(),
                EquipmentId = EquipmentId.From(Guid.CreateVersion7()),
                Code = $"{source.Code}-{index}",
                Form = source.Form with { ModbusUnitId = unitId, Secret = null },
            });
        }

        return rows;
    }

    public static (IReadOnlyList<StagingRowInput> Rows, IReadOnlyList<StagingRowResult> Errors) ParseCsv(string csv)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csv);
        if (csv.Length > MaximumCsvCharacters)
        {
            return ([], [Failure(Guid.Empty, default, "row", "staging.csv_size", "CSV exceeds the parser safety bound.")]);
        }

        List<List<string>> records;
        try
        {
            records = ParseRecords(csv);
        }
        catch (FormatException exception)
        {
            return ([], [Failure(Guid.Empty, default, "row", "staging.csv_syntax", exception.Message)]);
        }
        if (records.Count == 0 || !records[0].SequenceEqual(Headers, StringComparer.OrdinalIgnoreCase))
        {
            return ([], [Failure(Guid.Empty, default, "header", "staging.csv_header", "CSV header is invalid.")]);
        }

        var rows = new List<StagingRowInput>();
        var errors = new List<StagingRowResult>();
        for (var index = 1; index < records.Count; index++)
        {
            var values = records[index];
            if (values.Count != Headers.Length)
            {
                errors.Add(Failure(Guid.Empty, default, "row", "staging.csv_columns", $"CSV row {index + 1} has an invalid column count."));
                continue;
            }

            try
            {
                var protocol = values[6].Equals("modbus_tcp", StringComparison.OrdinalIgnoreCase)
                    ? EquipmentProtocol.ModbusTcp
                    : values[6].Equals("snmp", StringComparison.OrdinalIgnoreCase)
                        ? EquipmentProtocol.Snmp
                        : throw new FormatException("Unknown protocol.");
                var row = new StagingRowInput(
                    Guid.Parse(values[0]),
                    EquipmentId.From(Guid.Parse(values[1])),
                    FacilityScopeId.From(Guid.Parse(values[2])),
                    LocationId.From(Guid.Parse(values[3])),
                    values[4],
                    values[5],
                    new EquipmentProtocolForm(
                        protocol,
                        values[7],
                        int.Parse(values[8], CultureInfo.InvariantCulture),
                        NullInt(values[9]),
                        Null(values[10]),
                        Null(values[11]) is { } secret ? WriteOnlySecret.From(secret) : null));
                var validation = Validate(row);
                if (validation.Count == 0)
                {
                    rows.Add(row);
                }
                else
                {
                    errors.Add(new StagingRowResult(row.RowId, row.EquipmentId, null, validation));
                }
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
            {
                errors.Add(Failure(Guid.Empty, default, "row", "staging.csv_value", $"CSV row {index + 1}: {exception.Message}"));
            }
        }

        return (rows, errors);
    }

    private static List<List<string>> ParseRecords(string csv)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < csv.Length && csv[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(character);
                    EnsureFieldBound(field);
                }
            }
            else if (character == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (character == ',')
            {
                record.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n')
                {
                    index++;
                }

                record.Add(field.ToString());
                field.Clear();
                if (record.Any(value => value.Length > 0))
                {
                    records.Add(record);
                    EnsureRecordBound(records);
                }

                record = [];
            }
            else
            {
                field.Append(character);
                EnsureFieldBound(field);
            }
        }

        if (quoted)
        {
            throw new FormatException("CSV contains an unterminated quoted field.");
        }

        record.Add(field.ToString());
        if (record.Any(value => value.Length > 0))
        {
            records.Add(record);
            EnsureRecordBound(records);
        }

        return records;
    }

    private static void EnsureFieldBound(StringBuilder field)
    {
        if (field.Length > MaximumFieldCharacters)
        {
            throw new FormatException("CSV field exceeds the parser safety bound.");
        }
    }

    private static void EnsureRecordBound(List<List<string>> records)
    {
        if (records.Count > MaximumCsvRecords)
        {
            throw new FormatException("CSV exceeds the parser record safety bound.");
        }
    }

    private static int? NullInt(string value) => string.IsNullOrWhiteSpace(value)
        ? null
        : int.Parse(value, CultureInfo.InvariantCulture);

    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static StagingFieldError Error(string field, string code, string message) => new(field, code, message);

    private static StagingRowResult Failure(
        Guid rowId,
        EquipmentId equipmentId,
        string field,
        string code,
        string message) => new(rowId, equipmentId, null, [Error(field, code, message)]);
}
