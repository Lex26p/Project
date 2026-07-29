using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Equipment;

public enum StagingApplyAction
{
    Create = 1,
    Update = 2,
    Skip = 3,
}

public enum EquipmentDiagnosticMode
{
    ConnectionTest = 1,
    SamplePoll = 2,
}

public enum EquipmentDiagnosticJobStatus
{
    Pending = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    TimedOut = 5,
    Cancelled = 6,
}

public sealed record EquipmentStagingDraftInput(
    Guid RowId,
    EquipmentId EquipmentId,
    FacilityScopeId ScopeId,
    LocationId LocationId,
    string Code,
    string Name,
    EquipmentProtocol Protocol,
    string Host,
    int Port,
    int? ModbusUnitId,
    string? ModbusTable,
    int? ModbusAddress,
    string? ModbusValueType,
    string? ModbusByteOrder,
    string? ModbusWordOrder,
    decimal? ModbusScale,
    string? SnmpVersion,
    string? SnmpOid,
    string? SnmpValueType,
    string Unit,
    WriteOnlySecret? Secret,
    StagingApplyAction Action,
    long? ExpectedVersion)
{
    public decimal? SnmpScale { get; init; } = 1m;

    public static EquipmentStagingDraftInput New(
        FacilityScopeId scopeId,
        LocationId locationId,
        EquipmentProtocol protocol) =>
        new(
            Guid.CreateVersion7(),
            EquipmentId.From(Guid.CreateVersion7()),
            scopeId,
            locationId,
            string.Empty,
            string.Empty,
            protocol,
            string.Empty,
            protocol == EquipmentProtocol.ModbusTcp ? 502 : 161,
            protocol == EquipmentProtocol.ModbusTcp ? 1 : null,
            protocol == EquipmentProtocol.ModbusTcp ? "holding" : null,
            protocol == EquipmentProtocol.ModbusTcp ? 0 : null,
            protocol == EquipmentProtocol.ModbusTcp ? "signed16" : null,
            protocol == EquipmentProtocol.ModbusTcp ? "big" : null,
            protocol == EquipmentProtocol.ModbusTcp ? "high_first" : null,
            protocol == EquipmentProtocol.ModbusTcp ? 1m : null,
            protocol == EquipmentProtocol.Snmp ? "v2c" : null,
            protocol == EquipmentProtocol.Snmp ? "1.3.6.1.2.1.1.3.0" : null,
            protocol == EquipmentProtocol.Snmp ? "timeticks" : null,
            "-",
            protocol == EquipmentProtocol.Snmp ? WriteOnlySecret.From("public") : null,
            StagingApplyAction.Create,
            null);
}

public sealed record EquipmentStagingDraftSnapshot(
    Guid RowId,
    EquipmentId EquipmentId,
    FacilityScopeId ScopeId,
    LocationId LocationId,
    string Code,
    string Name,
    EquipmentProtocol Protocol,
    string Host,
    int Port,
    int? ModbusUnitId,
    string? ModbusTable,
    int? ModbusAddress,
    string? ModbusValueType,
    string? ModbusByteOrder,
    string? ModbusWordOrder,
    decimal? ModbusScale,
    string? SnmpVersion,
    string? SnmpOid,
    string? SnmpValueType,
    string Unit,
    bool HasSecret,
    StagingApplyAction Action,
    bool UpdateAuthorized,
    bool ExistingDeviceMatch,
    string Fingerprint,
    long Version,
    DateTimeOffset? AppliedAt,
    IReadOnlyList<StagingFieldError> Errors)
{
    public decimal? SnmpScale { get; init; } = 1m;
}

public sealed record EquipmentStagingTemplateSnapshot(
    Guid TemplateId,
    FacilityScopeId ScopeId,
    string Name,
    EquipmentProtocol Protocol,
    int Port,
    string? ModbusTable,
    int? ModbusAddress,
    string? ModbusValueType,
    string? ModbusByteOrder,
    string? ModbusWordOrder,
    decimal? ModbusScale,
    string? SnmpVersion,
    string? SnmpOid,
    string? SnmpValueType,
    string Unit,
    long Version)
{
    public decimal? SnmpScale { get; init; } = 1m;
}

public sealed record EquipmentStagingApplyResult(
    Guid RowId,
    StagingApplyAction Action,
    bool Succeeded,
    IReadOnlyList<StagingFieldError> Errors);

public sealed record EquipmentDiagnosticSample(
    string Name,
    decimal? Value,
    string? Unit,
    string Quality,
    DateTimeOffset ObservedAt,
    string? ErrorCode);

public sealed record EquipmentDiagnosticJobSnapshot(
    Guid JobId,
    Guid RowId,
    FacilityScopeId ScopeId,
    EquipmentDiagnosticMode Mode,
    EquipmentDiagnosticJobStatus Status,
    string Fingerprint,
    bool IsStale,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? OutcomeCode,
    string? OutcomeMessage,
    IReadOnlyList<EquipmentDiagnosticSample> Samples);

public sealed record EquipmentDiagnosticJobClaim(
    Guid JobId,
    Guid RowId,
    FacilityScopeId ScopeId,
    EquipmentDiagnosticMode Mode,
    string Fingerprint,
    string ManifestJson,
    string? SecretReference,
    Guid LeaseToken,
    string ClaimedBy,
    DateTimeOffset LeaseUntil,
    int Attempts);

public static class EquipmentCommissioningPermissions
{
    public static PermissionCode AuthorizeUpdate(FacilityScopeId scopeId) =>
        PermissionCode.From($"equipment.scope.s{scopeId.Value:N}.administer");
}
