using Dispatcher.Configuration;
using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Dispatcher.Server;

public static class EquipmentCommissioningEndpoints
{
    public static IServiceCollection AddEquipmentCommissioningServer(
        this IServiceCollection services,
        string connectionString,
        string equipmentDatabaseRole,
        string configurationDatabaseRole,
        byte[] stagingSecretKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(equipmentDatabaseRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationDatabaseRole);
        ArgumentNullException.ThrowIfNull(stagingSecretKey);
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.TryAddSingleton(sp => new EquipmentStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            equipmentDatabaseRole,
            sp.GetRequiredService<IWallClock>()));
        services.TryAddSingleton<EquipmentService>();
        services.AddSingleton(sp => new EquipmentStagingStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            equipmentDatabaseRole,
            sp.GetRequiredService<IWallClock>()));
        services.AddSingleton(sp => new InitialConfigurationStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            configurationDatabaseRole,
            sp.GetRequiredService<IWallClock>()));
        services.AddSingleton<InitialConfigurationService>();
        services.AddSingleton(sp => new ConfigurationStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            configurationDatabaseRole,
            sp.GetRequiredService<IWallClock>()));
        services.AddSingleton<ConfigurationService>();
        services.AddSingleton(new StagingSecretProtector(stagingSecretKey));
        services.AddSingleton<EquipmentStagingService>();
        services.AddSingleton<EquipmentCommissioningService>();
        return services;
    }

    public static IEndpointRouteBuilder MapEquipmentCommissioningServer(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/equipment-staging");
        group.MapGet("/", ReadDraftsAsync);
        group.MapPut("/{rowId:guid}", SaveDraftAsync);
        group.MapPost("/csv", ImportCsvAsync);
        group.MapPost("/{rowId:guid}/copy", CopyAsync);
        group.MapPost("/{rowId:guid}/authorize-update", AuthorizeUpdateAsync);
        group.MapGet("/templates", ReadTemplatesAsync);
        group.MapPost("/{rowId:guid}/templates", SaveTemplateAsync);
        group.MapPost("/templates/{templateId:guid}/apply", ApplyTemplateAsync);
        group.MapDelete("/templates/{templateId:guid}", DeleteTemplateAsync);
        group.MapPost("/apply", ApplyAsync);
        group.MapPost("/{rowId:guid}/diagnostics", StartDiagnosticAsync);
        group.MapGet("/{rowId:guid}/diagnostics/latest", ReadLatestDiagnosticAsync);
        group.MapGet("/diagnostics/{jobId:guid}", ReadDiagnosticAsync);

        var configuration = endpoints.MapGroup("/api/equipment-configuration");
        configuration.MapGet("/", ReadConfigurationAsync);
        configuration.MapPost("/save", SaveConfigurationAsync);
        configuration.MapPost("/save-staging", SaveStagedConfigurationAsync);
        configuration.MapPost("/validate", ValidateConfigurationAsync);
        configuration.MapPost("/publish", PublishConfigurationAsync);
        return endpoints;
    }

    private static async Task<IResult> ReadDraftsAsync(
        Guid scopeId,
        HttpContext context,
        RequestSessionResolver sessions,
        EquipmentCommissioningService service,
        CancellationToken cancellationToken) =>
        ToHttpResult(await service.ReadAsync(
            sessions.Resolve(context),
            FacilityScopeId.From(scopeId),
            cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> SaveDraftAsync(
        Guid rowId,
        EquipmentStagingDraftRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        EquipmentCommissioningService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = request.ToDomain(rowId);
            return ToHttpResult(await service.SaveAsync(
                sessions.Resolve(context), input, cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "staging.request_invalid", detail = exception.Message });
        }
    }

    private static async Task<IResult> ImportCsvAsync(
        EquipmentStagingCsvRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        EquipmentCommissioningService service,
        CancellationToken cancellationToken) =>
        ToHttpResult(await service.ImportCsvAsync(
            sessions.Resolve(context), request.Csv, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> CopyAsync(
        Guid rowId,
        Guid scopeId,
        EquipmentStagingCopyRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        EquipmentCommissioningService service,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToHttpResult(await service.CopyAsync(
                sessions.Resolve(context),
                FacilityScopeId.From(scopeId),
                rowId,
                request.Quantity,
                request.IncrementModbusUnitId,
                cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return Results.BadRequest(new { error = "staging.copy_invalid", detail = exception.Message });
        }
    }

    private static async Task<IResult> AuthorizeUpdateAsync(
        Guid rowId,
        Guid scopeId,
        EquipmentStagingVersionRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        EquipmentCommissioningService service,
        CancellationToken cancellationToken) =>
        ToHttpResult(await service.AuthorizeUpdateAsync(
            sessions.Resolve(context),
            FacilityScopeId.From(scopeId),
            rowId,
            request.ExpectedVersion,
            cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> ApplyAsync(
        EquipmentStagingApplyRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        EquipmentCommissioningService service,
        CancellationToken cancellationToken) =>
        ToHttpResult(await service.ApplyAsync(
            sessions.Resolve(context),
            FacilityScopeId.From(request.ScopeId),
            request.RowIds,
            cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> ReadTemplatesAsync(
        Guid scopeId,
        HttpContext context,
        RequestSessionResolver sessions,
        EquipmentCommissioningService service,
        CancellationToken cancellationToken) =>
        ToHttpResult(await service.ReadTemplatesAsync(
            sessions.Resolve(context),
            FacilityScopeId.From(scopeId),
            cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> SaveTemplateAsync(
        Guid rowId,
        Guid scopeId,
        EquipmentStagingTemplateSaveRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        EquipmentCommissioningService service,
        CancellationToken cancellationToken) =>
        ToHttpResult(await service.SaveTemplateAsync(
            sessions.Resolve(context),
            FacilityScopeId.From(scopeId),
            rowId,
            request.Name,
            cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> ApplyTemplateAsync(
        Guid templateId,
        EquipmentStagingTemplateApplyRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        EquipmentCommissioningService service,
        CancellationToken cancellationToken) =>
        ToHttpResult(await service.ApplyTemplateAsync(
            sessions.Resolve(context),
            FacilityScopeId.From(request.ScopeId),
            templateId,
            request.RowId,
            Dispatcher.Facilities.LocationId.From(request.LocationId),
            cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> DeleteTemplateAsync(
        Guid templateId,
        Guid scopeId,
        HttpContext context,
        RequestSessionResolver sessions,
        EquipmentCommissioningService service,
        CancellationToken cancellationToken) =>
        ToHttpResult(await service.DeleteTemplateAsync(
            sessions.Resolve(context),
            FacilityScopeId.From(scopeId),
            templateId,
            cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> StartDiagnosticAsync(
        Guid rowId,
        Guid scopeId,
        EquipmentDiagnosticRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        EquipmentCommissioningService service,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<EquipmentDiagnosticMode>(request.Mode, true, out var mode))
        {
            return Results.BadRequest(new { error = "diagnostic.mode_invalid", detail = "Diagnostic mode is invalid." });
        }

        return ToHttpResult(await service.StartDiagnosticAsync(
            sessions.Resolve(context),
            FacilityScopeId.From(scopeId),
            rowId,
            mode,
            cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> ReadDiagnosticAsync(
        Guid jobId,
        Guid scopeId,
        HttpContext context,
        RequestSessionResolver sessions,
        EquipmentCommissioningService service,
        CancellationToken cancellationToken) =>
        ToHttpResult(await service.ReadDiagnosticAsync(
            sessions.Resolve(context),
            FacilityScopeId.From(scopeId),
            jobId,
            cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> ReadLatestDiagnosticAsync(
        Guid rowId,
        Guid scopeId,
        HttpContext context,
        RequestSessionResolver sessions,
        EquipmentCommissioningService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ReadLatestDiagnosticAsync(
            sessions.Resolve(context),
            FacilityScopeId.From(scopeId),
            rowId,
            cancellationToken).ConfigureAwait(false);
        if (result.IsFailure && result.Error!.Code.Value == "diagnostic.job_not_found")
        {
            return Results.NoContent();
        }

        return result.IsFailure
            ? ToHttpResult(result)
            : Results.Ok(result.Value);
    }

    private static async Task<IResult> ReadConfigurationAsync(
        Guid scopeId,
        HttpContext context,
        RequestSessionResolver sessions,
        ConfigurationService service,
        CancellationToken cancellationToken) =>
        ToHttpResult(await service.ReadScopeAsync(
            sessions.Resolve(context),
            FacilityScopeId.From(scopeId),
            cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> SaveConfigurationAsync(
        EquipmentConfigurationSaveRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        ConfigurationService service,
        CancellationToken cancellationToken) =>
        ToHttpResult(await service.SaveAsync(
            sessions.Resolve(context),
            FacilityScopeId.From(request.ScopeId),
            new SaveConfigurationRequest(
                request.ManifestJson,
                request.Dependencies.Select(item =>
                    new ConfigurationDependency(item.Key, item.Fingerprint)).ToArray(),
                request.ExpectedVersion),
            cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> ValidateConfigurationAsync(
        EquipmentConfigurationRevisionRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        ConfigurationService service,
        CancellationToken cancellationToken) =>
        ToHttpResult(await service.ValidateAsync(
            sessions.Resolve(context),
            FacilityScopeId.From(request.ScopeId),
            ConfigurationRevisionId.From(request.RevisionId),
            request.ExpectedVersion,
            cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> SaveStagedConfigurationAsync(
        EquipmentStagedConfigurationSaveRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        EquipmentCommissioningService service,
        CancellationToken cancellationToken) =>
        ToHttpResult(await service.SaveAppliedConfigurationAsync(
            sessions.Resolve(context),
            FacilityScopeId.From(request.ScopeId),
            request.BaseManifestJson,
            request.ExpectedVersion,
            cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> PublishConfigurationAsync(
        EquipmentConfigurationPublishRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        ConfigurationService service,
        CancellationToken cancellationToken) =>
        ToHttpResult(await service.PublishAsync(
            sessions.Resolve(context),
            FacilityScopeId.From(request.ScopeId),
            new PublishConfigurationRequest(
                ConfigurationRevisionId.From(request.RevisionId),
                request.ExpectedVersion,
                request.Dependencies.Select(item =>
                    new ConfigurationDependency(item.Key, item.Fingerprint)).ToArray()),
            cancellationToken).ConfigureAwait(false));

    private static IResult ToHttpResult<T>(Result<T> result) => result.IsSuccess
        ? Results.Ok(result.Value)
        : Results.Problem(
            statusCode: StatusCode(result.Error!.Code.Value),
            title: result.Error.Code.Value,
            detail: result.Error.Message);

    private static IResult ToHttpResult(Result result) => result.IsSuccess
        ? Results.NoContent()
        : Results.Problem(
            statusCode: StatusCode(result.Error!.Code.Value),
            title: result.Error.Code.Value,
            detail: result.Error.Message);

    private static int StatusCode(string code) => code switch
    {
        "session.anonymous" or "session.revoked" or "session.expired" => StatusCodes.Status401Unauthorized,
        "permission.denied" => StatusCodes.Status403Forbidden,
        "staging.row_not_found" or "diagnostic.job_not_found" => StatusCodes.Status404NotFound,
        "staging.version_conflict" or "staging.stale_fingerprint" or
        "configuration.version_conflict" or "configuration.validation_stale" =>
            StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };
}

public sealed record EquipmentStagingDraftRequest(
    Guid EquipmentId,
    Guid ScopeId,
    Guid LocationId,
    string Code,
    string Name,
    string Protocol,
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
    string? Secret,
    string Action,
    long? ExpectedVersion)
{
    public decimal? SnmpScale { get; init; } = 1m;

    public EquipmentStagingDraftInput ToDomain(Guid rowId) =>
        new(
            rowId,
            Dispatcher.Equipment.EquipmentId.From(EquipmentId),
            FacilityScopeId.From(ScopeId),
            Dispatcher.Facilities.LocationId.From(LocationId),
            Code,
            Name,
            Protocol.Equals("modbus_tcp", StringComparison.OrdinalIgnoreCase)
                ? EquipmentProtocol.ModbusTcp
                : Protocol.Equals("snmp_v2c", StringComparison.OrdinalIgnoreCase)
                    ? EquipmentProtocol.Snmp
                    : throw new ArgumentException("Unsupported protocol."),
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
            Unit,
            string.IsNullOrEmpty(Secret) ? null : WriteOnlySecret.From(Secret),
            Enum.TryParse<StagingApplyAction>(Action, true, out var action)
                ? action
                : throw new ArgumentException("Unsupported staging action."),
            ExpectedVersion)
        {
            SnmpScale = SnmpScale,
        };
}

public sealed record EquipmentStagingCsvRequest(string Csv);
public sealed record EquipmentStagingCopyRequest(int Quantity, bool IncrementModbusUnitId);
public sealed record EquipmentStagingVersionRequest(long ExpectedVersion);
public sealed record EquipmentStagingApplyRequest(Guid ScopeId, IReadOnlyList<Guid> RowIds);
public sealed record EquipmentStagingTemplateSaveRequest(string Name);
public sealed record EquipmentStagingTemplateApplyRequest(Guid ScopeId, Guid LocationId, Guid? RowId);
public sealed record EquipmentDiagnosticRequest(string Mode);
public sealed record EquipmentConfigurationDependencyRequest(string Key, string Fingerprint);
public sealed record EquipmentConfigurationSaveRequest(
    Guid ScopeId,
    string ManifestJson,
    IReadOnlyList<EquipmentConfigurationDependencyRequest> Dependencies,
    long? ExpectedVersion);
public sealed record EquipmentStagedConfigurationSaveRequest(
    Guid ScopeId,
    string BaseManifestJson,
    long? ExpectedVersion);
public sealed record EquipmentConfigurationRevisionRequest(Guid ScopeId, Guid RevisionId, long ExpectedVersion);
public sealed record EquipmentConfigurationPublishRequest(
    Guid ScopeId,
    Guid RevisionId,
    long ExpectedVersion,
    IReadOnlyList<EquipmentConfigurationDependencyRequest> Dependencies);
