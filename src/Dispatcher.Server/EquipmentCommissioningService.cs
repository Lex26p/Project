using Dispatcher.Configuration;
using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using System.Text.Json;

namespace Dispatcher.Server;

public sealed record EquipmentStagingBatchResult(
    IReadOnlyList<EquipmentStagingDraftSnapshot> Rows,
    IReadOnlyList<StagingRowResult> Errors);

public sealed class EquipmentCommissioningService
{
    private readonly EquipmentStagingStore staging;
    private readonly EquipmentStagingService applyService;
    private readonly StagingSecretProtector protector;
    private readonly ConfigurationService configuration;
    private readonly IWallClock clock;

    public EquipmentCommissioningService(
        EquipmentStagingStore staging,
        EquipmentStagingService applyService,
        StagingSecretProtector protector,
        ConfigurationService configuration,
        IWallClock clock)
    {
        this.staging = staging;
        this.applyService = applyService;
        this.protector = protector;
        this.configuration = configuration;
        this.clock = clock;
    }

    public async Task<Result<IReadOnlyList<EquipmentStagingDraftSnapshot>>> ReadAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        var access = SessionAuthorization.AuthorizeAccess(session, EquipmentPermissions.Read(scopeId), clock);
        return access.IsFailure
            ? Result.Failure<IReadOnlyList<EquipmentStagingDraftSnapshot>>(access.Error!)
            : await staging.ReadDraftsAsync(scopeId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<EquipmentStagingDraftSnapshot>> SaveAsync(
        SessionSnapshot? session,
        EquipmentStagingDraftInput input,
        CancellationToken cancellationToken = default)
    {
        var access = SessionAuthorization.AuthorizeAccess(
            session, EquipmentPermissions.Write(input.ScopeId), clock);
        return access.IsFailure
            ? Result.Failure<EquipmentStagingDraftSnapshot>(access.Error!)
            : await staging.SaveDraftAsync(access.Value, input, protector, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<EquipmentStagingBatchResult>> ImportCsvAsync(
        SessionSnapshot? session,
        string csv,
        CancellationToken cancellationToken = default)
    {
        var parsed = EquipmentStagingTools.ParseCsv(csv);
        if (parsed.Rows.Count == 0)
        {
            return Result.Success(new EquipmentStagingBatchResult([], parsed.Errors));
        }

        var scopeId = parsed.Rows[0].ScopeId;
        var access = SessionAuthorization.AuthorizeAccess(session, EquipmentPermissions.Write(scopeId), clock);
        if (access.IsFailure)
        {
            return Result.Failure<EquipmentStagingBatchResult>(access.Error!);
        }

        var rows = new List<EquipmentStagingDraftSnapshot>(parsed.Rows.Count);
        var errors = new List<StagingRowResult>(parsed.Errors);
        foreach (var row in parsed.Rows)
        {
            if (row.ScopeId != scopeId)
            {
                errors.Add(new StagingRowResult(
                    row.RowId,
                    row.EquipmentId,
                    null,
                    [new StagingFieldError(
                        "scope_id",
                        "staging.csv_scope",
                        "One CSV upload must contain a single scope.")]));
                continue;
            }

            var draft = ToDraft(row);
            var saved = await staging.SaveDraftAsync(
                access.Value, draft, protector, cancellationToken).ConfigureAwait(false);
            if (saved.IsSuccess)
            {
                rows.Add(saved.Value);
            }
            else
            {
                errors.Add(new StagingRowResult(
                    row.RowId,
                    row.EquipmentId,
                    null,
                    [new StagingFieldError("row", saved.Error!.Code.Value, saved.Error.Message)]));
            }
        }

        return Result.Success(new EquipmentStagingBatchResult(rows, errors));
    }

    public async Task<Result<IReadOnlyList<EquipmentStagingDraftSnapshot>>> CopyAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        Guid rowId,
        int quantity,
        bool incrementModbusUnitId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);
        if (quantity > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Copy quantity cannot exceed 100.");
        }

        var access = SessionAuthorization.AuthorizeAccess(session, EquipmentPermissions.Write(scopeId), clock);
        if (access.IsFailure)
        {
            return Result.Failure<IReadOnlyList<EquipmentStagingDraftSnapshot>>(access.Error!);
        }

        var source = await staging.ReadDraftAsync(scopeId, rowId, cancellationToken).ConfigureAwait(false);
        if (source.IsFailure)
        {
            return Result.Failure<IReadOnlyList<EquipmentStagingDraftSnapshot>>(source.Error!);
        }

        var copies = new List<EquipmentStagingDraftSnapshot>(quantity);
        for (var index = 1; index <= quantity; index++)
        {
            var unitId = source.Value.ModbusUnitId;
            if (incrementModbusUnitId)
            {
                if (source.Value.Protocol != EquipmentProtocol.ModbusTcp ||
                    unitId is null ||
                    unitId + index > 255)
                {
                    return Failure<IReadOnlyList<EquipmentStagingDraftSnapshot>>(
                        "staging.copy_unit_id",
                        "Modbus Unit ID increment is invalid for this copy.");
                }

                unitId += index;
            }

            var input = ToInput(source.Value) with
            {
                RowId = Guid.CreateVersion7(),
                EquipmentId = EquipmentId.From(Guid.CreateVersion7()),
                Code = $"{source.Value.Code}-{index}",
                ModbusUnitId = unitId,
                Secret = null,
                ExpectedVersion = null,
                Action = StagingApplyAction.Create,
            };
            var saved = await staging.SaveDraftAsync(
                access.Value, input, protector, cancellationToken).ConfigureAwait(false);
            if (saved.IsFailure)
            {
                return Result.Failure<IReadOnlyList<EquipmentStagingDraftSnapshot>>(saved.Error!);
            }

            copies.Add(saved.Value);
        }

        return Result.Success<IReadOnlyList<EquipmentStagingDraftSnapshot>>(copies);
    }

    public async Task<Result<EquipmentStagingDraftSnapshot>> AuthorizeUpdateAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        Guid rowId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var access = SessionAuthorization.AuthorizeAccess(
            session, EquipmentCommissioningPermissions.AuthorizeUpdate(scopeId), clock);
        return access.IsFailure
            ? Result.Failure<EquipmentStagingDraftSnapshot>(access.Error!)
            : await staging.AuthorizeUpdateAsync(
                access.Value, scopeId, rowId, expectedVersion, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<IReadOnlyList<EquipmentStagingTemplateSnapshot>>> ReadTemplatesAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        var access = SessionAuthorization.AuthorizeAccess(session, EquipmentPermissions.Read(scopeId), clock);
        return access.IsFailure
            ? Result.Failure<IReadOnlyList<EquipmentStagingTemplateSnapshot>>(access.Error!)
            : await staging.ReadTemplatesAsync(scopeId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<EquipmentStagingTemplateSnapshot>> SaveTemplateAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        Guid rowId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var access = SessionAuthorization.AuthorizeAccess(session, EquipmentPermissions.Write(scopeId), clock);
        return access.IsFailure
            ? Result.Failure<EquipmentStagingTemplateSnapshot>(access.Error!)
            : await staging.SaveTemplateAsync(
                access.Value, scopeId, rowId, name, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<EquipmentStagingDraftSnapshot>> ApplyTemplateAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        Guid templateId,
        Guid? rowId,
        LocationId locationId,
        CancellationToken cancellationToken = default)
    {
        var access = SessionAuthorization.AuthorizeAccess(session, EquipmentPermissions.Write(scopeId), clock);
        if (access.IsFailure)
        {
            return Result.Failure<EquipmentStagingDraftSnapshot>(access.Error!);
        }

        var template = await staging.ReadTemplateAsync(
            scopeId, templateId, cancellationToken).ConfigureAwait(false);
        if (template.IsFailure)
        {
            return Result.Failure<EquipmentStagingDraftSnapshot>(template.Error!);
        }

        EquipmentStagingDraftInput input;
        if (rowId is not null)
        {
            var existing = await staging.ReadDraftAsync(
                scopeId, rowId.Value, cancellationToken).ConfigureAwait(false);
            if (existing.IsFailure)
            {
                return Result.Failure<EquipmentStagingDraftSnapshot>(existing.Error!);
            }

            input = ToInput(existing.Value) with
            {
                Protocol = template.Value.Protocol,
                Port = template.Value.Port,
                ModbusTable = template.Value.ModbusTable,
                ModbusAddress = template.Value.ModbusAddress,
                ModbusValueType = template.Value.ModbusValueType,
                ModbusByteOrder = template.Value.ModbusByteOrder,
                ModbusWordOrder = template.Value.ModbusWordOrder,
                ModbusScale = template.Value.ModbusScale,
                SnmpVersion = template.Value.SnmpVersion,
                SnmpOid = template.Value.SnmpOid,
                SnmpValueType = template.Value.SnmpValueType,
                SnmpScale = template.Value.SnmpScale,
                Unit = template.Value.Unit,
            };
        }
        else
        {
            input = EquipmentStagingDraftInput.New(scopeId, locationId, template.Value.Protocol) with
            {
                Port = template.Value.Port,
                ModbusTable = template.Value.ModbusTable,
                ModbusAddress = template.Value.ModbusAddress,
                ModbusValueType = template.Value.ModbusValueType,
                ModbusByteOrder = template.Value.ModbusByteOrder,
                ModbusWordOrder = template.Value.ModbusWordOrder,
                ModbusScale = template.Value.ModbusScale,
                SnmpVersion = template.Value.SnmpVersion,
                SnmpOid = template.Value.SnmpOid,
                SnmpValueType = template.Value.SnmpValueType,
                SnmpScale = template.Value.SnmpScale,
                Unit = template.Value.Unit,
                Secret = null,
            };
        }

        return await staging.SaveDraftAsync(
            access.Value, input, protector, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> DeleteTemplateAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        var access = SessionAuthorization.AuthorizeAccess(session, EquipmentPermissions.Write(scopeId), clock);
        return access.IsFailure
            ? Result.Failure(access.Error!)
            : await staging.DeleteTemplateAsync(
                access.Value, scopeId, templateId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<IReadOnlyList<EquipmentStagingApplyResult>>> ApplyAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        IReadOnlyCollection<Guid> rowIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rowIds);
        if (rowIds.Count is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(rowIds));
        }

        var access = SessionAuthorization.AuthorizeAccess(session, EquipmentPermissions.Write(scopeId), clock);
        if (access.IsFailure)
        {
            return Result.Failure<IReadOnlyList<EquipmentStagingApplyResult>>(access.Error!);
        }

        var outcomes = new List<EquipmentStagingApplyResult>(rowIds.Count);
        foreach (var rowId in rowIds.Distinct())
        {
            var prepared = await staging.PrepareApplyAsync(scopeId, rowId, cancellationToken).ConfigureAwait(false);
            if (prepared.IsFailure)
            {
                outcomes.Add(Failed(rowId, StagingApplyAction.Skip, prepared.Error!));
                continue;
            }

            var draft = prepared.Value.Draft;
            if (draft.Errors.Count > 0)
            {
                outcomes.Add(new EquipmentStagingApplyResult(rowId, draft.Action, false, draft.Errors));
                continue;
            }

            if (draft.Action == StagingApplyAction.Update && !draft.UpdateAuthorized)
            {
                outcomes.Add(new EquipmentStagingApplyResult(
                    rowId,
                    draft.Action,
                    false,
                    [new StagingFieldError(
                        "action",
                        "staging.update_not_authorized",
                        "Updating existing equipment requires explicit administrator authorization.")]));
                continue;
            }

            if (draft.Action == StagingApplyAction.Skip)
            {
                var skipped = await staging.MarkAppliedAsync(
                    access.Value, scopeId, rowId, draft.Fingerprint, cancellationToken).ConfigureAwait(false);
                outcomes.Add(skipped.IsSuccess
                    ? new EquipmentStagingApplyResult(rowId, draft.Action, true, [])
                    : Failed(rowId, draft.Action, skipped.Error!));
                continue;
            }

            var applied = AssertSingle(await applyService.ProcessAsync(
                session, [prepared.Value.Row], cancellationToken).ConfigureAwait(false));
            if (!applied.Created)
            {
                outcomes.Add(new EquipmentStagingApplyResult(rowId, draft.Action, false, applied.Errors));
                continue;
            }

            var marked = await staging.MarkAppliedAsync(
                access.Value, scopeId, rowId, draft.Fingerprint, cancellationToken).ConfigureAwait(false);
            outcomes.Add(marked.IsSuccess
                ? new EquipmentStagingApplyResult(rowId, draft.Action, true, [])
                : Failed(rowId, draft.Action, marked.Error!));
        }

        return Result.Success<IReadOnlyList<EquipmentStagingApplyResult>>(outcomes);
    }

    public async Task<Result<EquipmentDiagnosticJobSnapshot>> StartDiagnosticAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        Guid rowId,
        EquipmentDiagnosticMode mode,
        CancellationToken cancellationToken = default)
    {
        var access = SessionAuthorization.AuthorizeAccess(session, EquipmentPermissions.Write(scopeId), clock);
        return access.IsFailure
            ? Result.Failure<EquipmentDiagnosticJobSnapshot>(access.Error!)
            : await staging.EnqueueDiagnosticAsync(
                access.Value, scopeId, rowId, mode, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<EquipmentDiagnosticJobSnapshot>> ReadDiagnosticAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var access = SessionAuthorization.AuthorizeAccess(session, EquipmentPermissions.Read(scopeId), clock);
        return access.IsFailure
            ? Result.Failure<EquipmentDiagnosticJobSnapshot>(access.Error!)
            : await staging.ReadDiagnosticAsync(scopeId, jobId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<EquipmentDiagnosticJobSnapshot>> ReadLatestDiagnosticAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        Guid rowId,
        CancellationToken cancellationToken = default)
    {
        var access = SessionAuthorization.AuthorizeAccess(session, EquipmentPermissions.Read(scopeId), clock);
        return access.IsFailure
            ? Result.Failure<EquipmentDiagnosticJobSnapshot>(access.Error!)
            : await staging.ReadLatestDiagnosticAsync(scopeId, rowId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<ConfigurationRevisionSnapshot>> SaveAppliedConfigurationAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        string baseManifestJson,
        long? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var sources = await staging.BuildAppliedProtocolSourcesJsonAsync(
            scopeId, cancellationToken).ConfigureAwait(false);
        if (sources.IsFailure)
        {
            return Result.Failure<ConfigurationRevisionSnapshot>(sources.Error!);
        }

        string manifest;
        try
        {
            using var baseDocument = JsonDocument.Parse(baseManifestJson);
            using var protocolSources = JsonDocument.Parse(sources.Value);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in baseDocument.RootElement.EnumerateObject())
                {
                    if (!property.NameEquals("protocolSources"))
                    {
                        property.WriteTo(writer);
                    }
                }

                writer.WritePropertyName("protocolSources");
                protocolSources.RootElement.WriteTo(writer);
                writer.WriteEndObject();
            }

            manifest = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return Failure<ConfigurationRevisionSnapshot>(
                "configuration.manifest_invalid",
                "Base whole-scope manifest is invalid.");
        }

        return await configuration.SaveAsync(
            session,
            scopeId,
            new SaveConfigurationRequest(manifest, [], expectedVersion),
            cancellationToken).ConfigureAwait(false);
    }

    private static EquipmentStagingDraftInput ToDraft(StagingRowInput row) => new(
        row.RowId,
        row.EquipmentId,
        row.ScopeId,
        row.LocationId,
        row.Code,
        row.Name,
        row.Form.Protocol,
        row.Form.Host,
        row.Form.Port,
        row.Form.ModbusUnitId,
        row.Form.Protocol == EquipmentProtocol.ModbusTcp ? "holding" : null,
        row.Form.Protocol == EquipmentProtocol.ModbusTcp ? 0 : null,
        row.Form.Protocol == EquipmentProtocol.ModbusTcp ? "signed16" : null,
        row.Form.Protocol == EquipmentProtocol.ModbusTcp ? "big" : null,
        row.Form.Protocol == EquipmentProtocol.ModbusTcp ? "high_first" : null,
        row.Form.Protocol == EquipmentProtocol.ModbusTcp ? 1m : null,
        row.Form.SnmpVersion,
        row.Form.Protocol == EquipmentProtocol.Snmp ? "1.3.6.1.2.1.1.3.0" : null,
        row.Form.Protocol == EquipmentProtocol.Snmp ? "timeticks" : null,
        "-",
        row.Form.Secret,
        StagingApplyAction.Create,
        null);

    private static EquipmentStagingDraftInput ToInput(EquipmentStagingDraftSnapshot row) =>
        new(
            row.RowId,
            row.EquipmentId,
            row.ScopeId,
            row.LocationId,
            row.Code,
            row.Name,
            row.Protocol,
            row.Host,
            row.Port,
            row.ModbusUnitId,
            row.ModbusTable,
            row.ModbusAddress,
            row.ModbusValueType,
            row.ModbusByteOrder,
            row.ModbusWordOrder,
            row.ModbusScale,
            row.SnmpVersion,
            row.SnmpOid,
            row.SnmpValueType,
            row.Unit,
            null,
            row.Action,
            row.Version)
        {
            SnmpScale = row.SnmpScale,
        };

    private static StagingRowResult AssertSingle(IReadOnlyList<StagingRowResult> rows) =>
        rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException("Single staging apply returned an invalid result count.");

    private static EquipmentStagingApplyResult Failed(
        Guid rowId,
        StagingApplyAction action,
        OperationError error) =>
        new(rowId, action, false, [new StagingFieldError("row", error.Code.Value, error.Message)]);

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));
}
