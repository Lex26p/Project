using System.Net;
using System.Net.Http.Json;

namespace Dispatcher.Web;

public sealed class EquipmentCommissioningApiClient(HttpClient http)
{
    public async Task<IReadOnlyList<EquipmentStagingDraftPayload>> ReadDraftsAsync(
        Guid scopeId,
        CancellationToken token = default) =>
        await ReadAsync<EquipmentStagingDraftPayload[]>(
            $"api/equipment-staging?scopeId={scopeId:D}", token) ?? [];

    public async Task<EquipmentStagingDraftPayload> SaveDraftAsync(
        Guid rowId,
        EquipmentStagingDraftEdit request,
        CancellationToken token = default)
    {
        var response = await http.PutAsJsonAsync(
            $"api/equipment-staging/{rowId:D}", request.ToRequest(), token);
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<EquipmentStagingDraftPayload>(token)
            ?? throw new InvalidOperationException("Staging response was empty.");
    }

    public async Task<EquipmentStagingBatchPayload> ImportCsvAsync(
        string csv,
        CancellationToken token = default)
    {
        var response = await http.PostAsJsonAsync(
            "api/equipment-staging/csv", new { csv }, token);
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<EquipmentStagingBatchPayload>(token)
            ?? throw new InvalidOperationException("CSV staging response was empty.");
    }

    public async Task<IReadOnlyList<EquipmentStagingDraftPayload>> CopyAsync(
        Guid scopeId,
        Guid rowId,
        int quantity,
        bool incrementModbusUnitId,
        CancellationToken token = default)
    {
        var response = await http.PostAsJsonAsync(
            $"api/equipment-staging/{rowId:D}/copy?scopeId={scopeId:D}",
            new { quantity, incrementModbusUnitId },
            token);
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<EquipmentStagingDraftPayload[]>(token) ?? [];
    }

    public async Task<EquipmentStagingDraftPayload> AuthorizeUpdateAsync(
        Guid scopeId,
        Guid rowId,
        long expectedVersion,
        CancellationToken token = default)
    {
        var response = await http.PostAsJsonAsync(
            $"api/equipment-staging/{rowId:D}/authorize-update?scopeId={scopeId:D}",
            new { expectedVersion },
            token);
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<EquipmentStagingDraftPayload>(token)
            ?? throw new InvalidOperationException("Update authorization response was empty.");
    }

    public async Task<IReadOnlyList<EquipmentStagingApplyPayload>> ApplyAsync(
        Guid scopeId,
        IReadOnlyList<Guid> rowIds,
        CancellationToken token = default)
    {
        var response = await http.PostAsJsonAsync(
            "api/equipment-staging/apply", new { scopeId, rowIds }, token);
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<EquipmentStagingApplyPayload[]>(token) ?? [];
    }

    public async Task<EquipmentDiagnosticJobPayload> StartDiagnosticAsync(
        Guid scopeId,
        Guid rowId,
        string mode,
        CancellationToken token = default)
    {
        var response = await http.PostAsJsonAsync(
            $"api/equipment-staging/{rowId:D}/diagnostics?scopeId={scopeId:D}",
            new { mode },
            token);
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<EquipmentDiagnosticJobPayload>(token)
            ?? throw new InvalidOperationException("Diagnostic response was empty.");
    }

    public Task<EquipmentDiagnosticJobPayload?> ReadDiagnosticAsync(
        Guid scopeId,
        Guid jobId,
        CancellationToken token = default) =>
        ReadAsync<EquipmentDiagnosticJobPayload>(
            $"api/equipment-staging/diagnostics/{jobId:D}?scopeId={scopeId:D}", token);

    public Task<EquipmentDiagnosticJobPayload?> ReadLatestDiagnosticAsync(
        Guid scopeId,
        Guid rowId,
        CancellationToken token = default) =>
        ReadAsync<EquipmentDiagnosticJobPayload>(
            $"api/equipment-staging/{rowId:D}/diagnostics/latest?scopeId={scopeId:D}", token);

    public Task<EquipmentStagingTemplatePayload[]?> ReadTemplatesAsync(
        Guid scopeId,
        CancellationToken token = default) =>
        ReadAsync<EquipmentStagingTemplatePayload[]>(
            $"api/equipment-staging/templates?scopeId={scopeId:D}", token);

    public async Task<EquipmentStagingTemplatePayload> SaveTemplateAsync(
        Guid scopeId,
        Guid rowId,
        string name,
        CancellationToken token = default)
    {
        var response = await http.PostAsJsonAsync(
            $"api/equipment-staging/{rowId:D}/templates?scopeId={scopeId:D}",
            new { name },
            token);
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<EquipmentStagingTemplatePayload>(token)
            ?? throw new InvalidOperationException("Template response was empty.");
    }

    public async Task<EquipmentStagingDraftPayload> ApplyTemplateAsync(
        Guid scopeId,
        Guid locationId,
        Guid templateId,
        Guid? rowId,
        CancellationToken token = default)
    {
        var response = await http.PostAsJsonAsync(
            $"api/equipment-staging/templates/{templateId:D}/apply",
            new { scopeId, locationId, rowId },
            token);
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<EquipmentStagingDraftPayload>(token)
            ?? throw new InvalidOperationException("Template apply response was empty.");
    }

    public Task<EquipmentConfigurationScopePayload?> ReadConfigurationAsync(
        Guid scopeId,
        CancellationToken token = default) =>
        ReadAsync<EquipmentConfigurationScopePayload>(
            $"api/equipment-configuration?scopeId={scopeId:D}", token);

    public async Task<EquipmentConfigurationRevisionPayload> SaveConfigurationAsync(
        Guid scopeId,
        string baseManifestJson,
        long? expectedVersion,
        CancellationToken token = default)
    {
        var response = await http.PostAsJsonAsync(
            "api/equipment-configuration/save-staging",
            new { scopeId, baseManifestJson, expectedVersion },
            token);
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<EquipmentConfigurationRevisionPayload>(token)
            ?? throw new InvalidOperationException("Configuration save response was empty.");
    }

    public async Task<EquipmentConfigurationRevisionPayload> ValidateConfigurationAsync(
        Guid scopeId,
        Guid revisionId,
        long expectedVersion,
        CancellationToken token = default)
    {
        var response = await http.PostAsJsonAsync(
            "api/equipment-configuration/validate",
            new { scopeId, revisionId, expectedVersion },
            token);
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<EquipmentConfigurationRevisionPayload>(token)
            ?? throw new InvalidOperationException("Configuration validation response was empty.");
    }

    public async Task<EquipmentConfigurationRevisionPayload> PublishConfigurationAsync(
        Guid scopeId,
        Guid revisionId,
        long expectedVersion,
        CancellationToken token = default)
    {
        var response = await http.PostAsJsonAsync(
            "api/equipment-configuration/publish",
            new { scopeId, revisionId, expectedVersion, dependencies = Array.Empty<object>() },
            token);
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<EquipmentConfigurationRevisionPayload>(token)
            ?? throw new InvalidOperationException("Configuration publish response was empty.");
    }

    private async Task<T?> ReadAsync<T>(string uri, CancellationToken token)
    {
        var response = await http.GetAsync(uri, token);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return default;
        }

        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<T>(token);
    }

    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var problem = await response.Content.ReadFromJsonAsync<EquipmentStagingProblemPayload>();
        throw new EquipmentStagingApiException(
            problem?.Title ?? "staging.request_failed",
            problem?.Detail ?? "Equipment staging request failed.",
            response.StatusCode == HttpStatusCode.Conflict);
    }
}

public sealed class EquipmentStagingApiException(
    string code,
    string message,
    bool conflict) : Exception(message)
{
    public string Code { get; } = code;
    public bool IsConflict { get; } = conflict;
}

public sealed record EquipmentStagingProblemPayload(string? Title, string? Detail);
public sealed record EquipmentIdPayload(Guid Value);
public sealed record FacilityScopeIdPayload(Guid Value);
public sealed record LocationIdPayload(Guid Value);
public sealed record EquipmentStagingFieldErrorPayload(string Field, string Code, string Message);
public sealed record EquipmentStagingDraftPayload(
    Guid RowId,
    EquipmentIdPayload EquipmentId,
    FacilityScopeIdPayload ScopeId,
    LocationIdPayload LocationId,
    string Code,
    string Name,
    int Protocol,
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
    int Action,
    bool UpdateAuthorized,
    bool ExistingDeviceMatch,
    string Fingerprint,
    long Version,
    DateTimeOffset? AppliedAt,
    IReadOnlyList<EquipmentStagingFieldErrorPayload> Errors)
{
    public decimal? SnmpScale { get; init; } = 1m;
}
public sealed record EquipmentStagingRowResultPayload(
    Guid RowId,
    EquipmentIdPayload EquipmentId,
    int? State,
    IReadOnlyList<EquipmentStagingFieldErrorPayload> Errors);
public sealed record EquipmentStagingBatchPayload(
    IReadOnlyList<EquipmentStagingDraftPayload> Rows,
    IReadOnlyList<EquipmentStagingRowResultPayload> Errors);
public sealed record EquipmentStagingApplyPayload(
    Guid RowId,
    int Action,
    bool Succeeded,
    IReadOnlyList<EquipmentStagingFieldErrorPayload> Errors);
public sealed record EquipmentDiagnosticSamplePayload(
    string Name,
    decimal? Value,
    string? Unit,
    string Quality,
    DateTimeOffset ObservedAt,
    string? ErrorCode);
public sealed record EquipmentDiagnosticJobPayload(
    Guid JobId,
    Guid RowId,
    FacilityScopeIdPayload ScopeId,
    int Mode,
    int Status,
    string Fingerprint,
    bool IsStale,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? OutcomeCode,
    string? OutcomeMessage,
    IReadOnlyList<EquipmentDiagnosticSamplePayload> Samples);
public sealed record EquipmentStagingTemplatePayload(
    Guid TemplateId,
    FacilityScopeIdPayload ScopeId,
    string Name,
    int Protocol,
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
public sealed record ConfigurationRevisionIdPayload(Guid Value);
public sealed record EquipmentConfigurationRevisionPayload(
    ConfigurationRevisionIdPayload RevisionId,
    FacilityScopeIdPayload ScopeId,
    ulong RevisionNumber,
    ConfigurationRevisionIdPayload? SourceRevisionId,
    string ManifestJson,
    string ManifestFingerprint,
    IReadOnlyList<object> Dependencies,
    string DependencyFingerprint,
    long Version,
    DateTimeOffset SavedAt,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? DistributedAt,
    DateTimeOffset? ActivatedAt);
public sealed record EquipmentConfigurationScopePayload(
    FacilityScopeIdPayload ScopeId,
    ConfigurationRevisionIdPayload? DraftRevisionId,
    ConfigurationRevisionIdPayload? PublishedRevisionId,
    ConfigurationRevisionIdPayload? DistributedRevisionId,
    ConfigurationRevisionIdPayload? ActivatedRevisionId,
    long Version,
    IReadOnlyList<EquipmentConfigurationRevisionPayload> Revisions);

public sealed class EquipmentStagingDraftEdit
{
    public Guid RowId { get; set; }
    public Guid EquipmentId { get; set; }
    public Guid ScopeId { get; set; }
    public Guid LocationId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Protocol { get; set; } = "modbus_tcp";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 502;
    public int? ModbusUnitId { get; set; } = 1;
    public string? ModbusTable { get; set; } = "holding";
    public int? ModbusAddress { get; set; } = 0;
    public string? ModbusValueType { get; set; } = "signed16";
    public string? ModbusByteOrder { get; set; } = "big";
    public string? ModbusWordOrder { get; set; } = "high_first";
    public decimal? ModbusScale { get; set; } = 1m;
    public string? SnmpVersion { get; set; }
    public string? SnmpOid { get; set; }
    public string? SnmpValueType { get; set; }
    public decimal? SnmpScale { get; set; } = 1m;
    public string Unit { get; set; } = "-";
    public string? Secret { get; set; }
    public string Action { get; set; } = "Create";
    public long? ExpectedVersion { get; set; }

    public static EquipmentStagingDraftEdit New(
        Guid scopeId,
        Guid locationId,
        string protocol)
    {
        var snmp = protocol == "snmp_v2c";
        return new EquipmentStagingDraftEdit
        {
            RowId = Guid.CreateVersion7(),
            EquipmentId = Guid.CreateVersion7(),
            ScopeId = scopeId,
            LocationId = locationId,
            Protocol = protocol,
            Port = snmp ? 161 : 502,
            ModbusUnitId = snmp ? null : 1,
            ModbusTable = snmp ? null : "holding",
            ModbusAddress = snmp ? null : 0,
            ModbusValueType = snmp ? null : "signed16",
            ModbusByteOrder = snmp ? null : "big",
            ModbusWordOrder = snmp ? null : "high_first",
            ModbusScale = snmp ? null : 1m,
            SnmpVersion = snmp ? "v2c" : null,
            SnmpOid = snmp ? "1.3.6.1.2.1.1.3.0" : null,
            SnmpValueType = snmp ? "timeticks" : null,
            SnmpScale = snmp ? 1m : null,
            Secret = snmp ? "public" : null,
        };
    }

    public static EquipmentStagingDraftEdit From(EquipmentStagingDraftPayload row) => new()
    {
        RowId = row.RowId,
        EquipmentId = row.EquipmentId.Value,
        ScopeId = row.ScopeId.Value,
        LocationId = row.LocationId.Value,
        Code = row.Code,
        Name = row.Name,
        Protocol = row.Protocol == 1 ? "modbus_tcp" : "snmp_v2c",
        Host = row.Host,
        Port = row.Port,
        ModbusUnitId = row.ModbusUnitId,
        ModbusTable = row.ModbusTable,
        ModbusAddress = row.ModbusAddress,
        ModbusValueType = row.ModbusValueType,
        ModbusByteOrder = row.ModbusByteOrder,
        ModbusWordOrder = row.ModbusWordOrder,
        ModbusScale = row.ModbusScale,
        SnmpVersion = row.SnmpVersion,
        SnmpOid = row.SnmpOid,
        SnmpValueType = row.SnmpValueType,
        SnmpScale = row.SnmpScale,
        Unit = row.Unit,
        Action = ((StagingActionPayload)row.Action).ToString(),
        ExpectedVersion = row.Version,
    };

    public object ToRequest() => new
    {
        EquipmentId,
        ScopeId,
        LocationId,
        Code,
        Name,
        Protocol,
        Host,
        Port,
        ModbusUnitId,
        ModbusTable,
        ModbusAddress,
        ModbusValueType,
        ModbusByteOrder,
        ModbusWordOrder,
        ModbusScale,
        SnmpVersion,
        SnmpOid,
        SnmpValueType,
        SnmpScale,
        Unit,
        Secret,
        Action,
        ExpectedVersion,
    };
}

public enum StagingActionPayload
{
    Create = 1,
    Update = 2,
    Skip = 3,
}
