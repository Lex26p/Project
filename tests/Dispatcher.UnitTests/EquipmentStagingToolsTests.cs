using System.Text.Json;
using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class EquipmentStagingToolsTests
{
    [Fact]
    public void ManualCopyAndTemplatesUseOneContractWithoutCopyingSecrets()
    {
        var source = Row(EquipmentProtocolForm.NewModbusTcp() with
        {
            Host = "192.0.2.10",
            ModbusUnitId = 10,
        });
        var copies = EquipmentStagingTools.Copy(source, 2, incrementModbusUnitId: true);

        Assert.Equal([11, 12], copies.Select(row => row.Form.ModbusUnitId));
        Assert.All(copies, row => Assert.Equal("192.0.2.10", row.Form.Host));
        Assert.Equal(["EQ-1-1", "EQ-1-2"], copies.Select(row => row.Code));

        var freshSnmp = EquipmentProtocolForm.NewSnmp();
        Assert.NotNull(freshSnmp.Secret);
        Assert.Equal("[REDACTED]", freshSnmp.Secret.ToString());
        var template = new EquipmentStagingTemplate(
            "SNMP switch",
            EquipmentProtocol.Snmp,
            "switch.example",
            161,
            null,
            "v2c");
        Assert.Null(template.CreateForm().Secret);
        Assert.DoesNotContain("public", JsonSerializer.Serialize(template), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CsvProducesPerRowResultsAndNeverDefinesDeletion()
    {
        var scopeId = Guid.Parse("81000000-0000-0000-0000-000000000001");
        var locationId = Guid.Parse("82000000-0000-0000-0000-000000000001");
        var valid = string.Join(',',
            Guid.Parse("83000000-0000-0000-0000-000000000001"),
            Guid.Parse("84000000-0000-0000-0000-000000000001"),
            scopeId,
            locationId,
            "EQ-CSV",
            "CSV equipment",
            "modbus_tcp",
            "plc.example",
            "502",
            "1",
            string.Empty,
            string.Empty);
        var invalid = string.Join(',',
            Guid.Parse("83000000-0000-0000-0000-000000000002"),
            Guid.Parse("84000000-0000-0000-0000-000000000002"),
            scopeId,
            locationId,
            string.Empty,
            "Bad row",
            "snmp",
            "switch.example",
            "70000",
            string.Empty,
            "v3",
            string.Empty);
        var csv = "row_id,equipment_id,scope_id,location_id,code,name,protocol,host,port,modbus_unit_id,snmp_version,secret\n" +
                  valid + "\n" + invalid;

        var parsed = EquipmentStagingTools.ParseCsv(csv);
        Assert.Single(parsed.Rows);
        var error = Assert.Single(parsed.Errors);
        Assert.Contains(error.Errors, item => item.Field == "code");
        Assert.Contains(error.Errors, item => item.Field == "port");
        Assert.Contains(error.Errors, item => item.Field == "snmp_version");
        Assert.Contains(error.Errors, item => item.Field == "secret");

        var hostile = EquipmentStagingTools.ParseCsv("\"unterminated");
        Assert.Empty(hostile.Rows);
        Assert.Equal("staging.csv_syntax", Assert.Single(hostile.Errors).Errors[0].Code);
        var deleteHeader = EquipmentStagingTools.ParseCsv("action,row_id\ndelete," + Guid.NewGuid());
        Assert.Equal("staging.csv_header", Assert.Single(deleteHeader.Errors).Errors[0].Code);
    }

    private static StagingRowInput Row(EquipmentProtocolForm form) => new(
        Guid.Parse("83000000-0000-0000-0000-000000000010"),
        EquipmentId.From(Guid.Parse("84000000-0000-0000-0000-000000000010")),
        FacilityScopeId.From(Guid.Parse("81000000-0000-0000-0000-000000000010")),
        LocationId.From(Guid.Parse("82000000-0000-0000-0000-000000000010")),
        "EQ-1",
        "Equipment",
        form);
}
