using System.Text.Json;
using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;
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

    [Fact]
    public void CommissioningDraftValidationKeepsProtocolFieldsSeparate()
    {
        var scopeId = FacilityScopeId.From(Guid.Parse("81000000-0000-0000-0000-000000000020"));
        var locationId = LocationId.From(Guid.Parse("82000000-0000-0000-0000-000000000020"));
        var modbus = EquipmentStagingDraftInput.New(
            scopeId, locationId, EquipmentProtocol.ModbusTcp) with
        {
            Code = "PLC-C14",
            Name = "PLC",
            Host = "127.0.0.1",
        };
        Assert.Empty(EquipmentCommissioningTools.ValidateDraft(modbus, hasSecret: false));

        var snmp = EquipmentStagingDraftInput.New(
            scopeId, locationId, EquipmentProtocol.Snmp) with
        {
            Code = "SW-C14",
            Name = "Switch",
            Host = "127.0.0.1",
            SnmpVersion = "v3",
            SnmpOid = string.Empty,
            SnmpValueType = "octet_string",
            SnmpScale = 0m,
            Secret = null,
        };
        var errors = EquipmentCommissioningTools.ValidateDraft(snmp, hasSecret: false);
        Assert.Contains(errors, error => error.Field == "snmp_version");
        Assert.Contains(errors, error => error.Field == "snmp_oid");
        Assert.Contains(errors, error => error.Field == "snmp_value_type");
        Assert.Contains(errors, error => error.Field == "snmp_scale");
        Assert.Contains(errors, error => error.Field == "secret");
    }

    [Fact]
    public void ExistingDeviceUpdateRequiresExplicitAdministerPermission()
    {
        var now = new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);
        var scopeId = FacilityScopeId.From(
            Guid.Parse("81000000-0000-0000-0000-000000000030"));
        var permission = EquipmentCommissioningPermissions.AuthorizeUpdate(scopeId);
        var writeOnly = new SessionSnapshot(
            SessionId.New(),
            SubjectId.New(),
            PrincipalKind.User,
            now.AddMinutes(-1),
            now.AddMinutes(30),
            new EffectivePermissions([EquipmentPermissions.Write(scopeId)]));
        var administrator = new SessionSnapshot(
            SessionId.New(),
            SubjectId.New(),
            PrincipalKind.User,
            now.AddMinutes(-1),
            now.AddMinutes(30),
            new EffectivePermissions(
                [EquipmentPermissions.Write(scopeId), permission]));
        var clock = new FixedClock(now);

        Assert.Equal(
            "permission.denied",
            SessionAuthorization.AuthorizeAccess(writeOnly, permission, clock).Error?.Code.Value);
        Assert.True(
            SessionAuthorization.AuthorizeAccess(administrator, permission, clock).IsSuccess);
    }

    private static StagingRowInput Row(EquipmentProtocolForm form) => new(
        Guid.Parse("83000000-0000-0000-0000-000000000010"),
        EquipmentId.From(Guid.Parse("84000000-0000-0000-0000-000000000010")),
        FacilityScopeId.From(Guid.Parse("81000000-0000-0000-0000-000000000010")),
        LocationId.From(Guid.Parse("82000000-0000-0000-0000-000000000010")),
        "EQ-1",
        "Equipment",
        form);

    private sealed class FixedClock(DateTimeOffset now) : IWallClock
    {
        public DateTimeOffset GetUtcNow() => now;
    }
}
