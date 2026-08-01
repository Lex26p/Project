using Dispatcher.Equipment;
using Dispatcher.Events;
using Dispatcher.Facilities;
using Dispatcher.Maintenance;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Dispatcher.Server;

public static class MaintenanceEndpoints
{
    public static IServiceCollection AddMaintenanceServer(
        this IServiceCollection services,
        string connectionString,
        string databaseRole,
        MaintenanceQueryLimits queryLimits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        ArgumentNullException.ThrowIfNull(queryLimits);
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(queryLimits);
        services.AddSingleton(sp => new MaintenanceStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            databaseRole,
            sp.GetRequiredService<IWallClock>()));
        services.AddSingleton(sp => new MaintenanceService(
            sp.GetRequiredService<MaintenanceStore>(),
            sp.GetRequiredService<EquipmentStore>(),
            sp.GetRequiredService<IWallClock>(),
            sp.GetRequiredService<MaintenanceQueryLimits>()));
        services.AddSingleton(sp => new MaintenanceSchedulerStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            databaseRole,
            sp.GetRequiredService<IWallClock>()));
        services.AddSingleton(sp => new MaintenancePlanningService(
            sp.GetRequiredService<MaintenanceSchedulerStore>(),
            sp.GetRequiredService<IWallClock>(),
            sp.GetRequiredService<MaintenanceQueryLimits>()));
        services.AddSingleton(sp => new MaintenanceWorkStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            databaseRole,
            sp.GetRequiredService<IWallClock>()));
        services.AddSingleton(sp => new MaintenanceWorkService(
            sp.GetRequiredService<MaintenanceWorkStore>(),
            sp.GetRequiredService<MaintenanceStore>(),
            sp.GetRequiredService<IWallClock>(),
            sp.GetRequiredService<MaintenanceQueryLimits>()));
        services.AddSingleton<MaintenanceEventApplicationService>();
        services.AddSingleton<MaintenanceWorkApplicationService>();
        return services;
    }

    public static IEndpointRouteBuilder MapMaintenanceServer(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/maintenance");
        group.MapGet("/assets", QueryAssetsAsync);
        group.MapGet("/assets/{assetId:guid}", ReadAssetAsync);
        group.MapGet("/assets/{assetId:guid}/link-history", ReadLinkHistoryAsync);
        group.MapPost("/assets", CreateAssetAsync);
        group.MapPut("/assets/{assetId:guid}", UpdateAssetAsync);
        group.MapPost("/assets/{assetId:guid}/equipment-link", LinkEquipmentAsync);
        group.MapPost("/assets/{assetId:guid}/equipment-link/confirm", ConfirmEquipmentLinkAsync);
        group.MapPost("/assets/{assetId:guid}/equipment-link/unlink", UnlinkEquipmentAsync);
        group.MapGet("/plans/{planId:guid}", ReadPlanAsync);
        group.MapGet("/forecast", QueryForecastAsync);
        group.MapGet("/calendar", QueryCalendarAsync);
        MaintenanceWorkEndpoints.Map(group);
        return endpoints;
    }

    private static async Task<IResult> ReadPlanAsync(
        Guid planId,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenancePlanningService planning,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        return ToHttpResult(
            await planning.GetPlanAsync(
                session,
                MaintenancePlanId.From(planId),
                cancellationToken).ConfigureAwait(false),
            plan => new MaintenancePlanPayload(
                plan.Plan.PlanId.Value,
                plan.Plan.AssetId.Value,
                plan.ScopeId.Value,
                plan.Plan.Revision.Value,
                plan.Plan.Title,
                plan.Plan.FirstDueOn,
                plan.Plan.Recurrence.IntervalDays,
                plan.Plan.EffectiveThrough,
                $"/maintenance/assets/{plan.Plan.AssetId.Value:D}",
                session?.Permissions.Allows(
                    MaintenanceWorkPermissions.Manage(plan.ScopeId)) == true,
                plan.CreatedAt,
                plan.UpdatedAt));
    }

    private static Task<IResult> QueryForecastAsync(
        Guid scopeId,
        DateOnly from,
        DateOnly to,
        int pageSize,
        DateOnly? afterDueOn,
        Guid? afterObligationId,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenancePlanningService planning,
        IWallClock clock,
        CancellationToken cancellationToken) =>
        QueryPlanningAsync(
            false,
            scopeId,
            from,
            to,
            pageSize,
            afterDueOn,
            afterObligationId,
            context,
            sessions,
            planning,
            clock,
            cancellationToken);

    private static Task<IResult> QueryCalendarAsync(
        Guid scopeId,
        DateOnly from,
        DateOnly to,
        int pageSize,
        DateOnly? afterDueOn,
        Guid? afterObligationId,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenancePlanningService planning,
        IWallClock clock,
        CancellationToken cancellationToken) =>
        QueryPlanningAsync(
            true,
            scopeId,
            from,
            to,
            pageSize,
            afterDueOn,
            afterObligationId,
            context,
            sessions,
            planning,
            clock,
            cancellationToken);

    private static async Task<IResult> QueryPlanningAsync(
        bool calendar,
        Guid scopeId,
        DateOnly from,
        DateOnly to,
        int pageSize,
        DateOnly? afterDueOn,
        Guid? afterObligationId,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenancePlanningService planning,
        IWallClock clock,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        var query = new MaintenanceForecastQuery(
            FacilityScopeId.From(scopeId),
            from,
            to,
            pageSize,
            afterDueOn,
            afterObligationId is null
                ? null
                : MaintenanceForecastObligationId.From(afterObligationId.Value));
        var result = calendar
            ? await planning.QueryCalendarAsync(session, query, cancellationToken).ConfigureAwait(false)
            : await planning.QueryForecastAsync(session, query, cancellationToken).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return ToHttpResult(result, page => new MaintenanceForecastPagePayload(
            page.Entries.Select(entry => new MaintenanceForecastPayload(
                entry.ObligationId.Value,
                entry.WorkOrderId.Value,
                entry.PlanId.Value,
                entry.AssetId.Value,
                entry.ScopeId.Value,
                entry.PlanRevision.Value,
                entry.Title,
                entry.DueOn,
                entry.State.ToString(),
                entry.IsOverdue(today),
                entry.State == MaintenanceMaterializationState.Completed
                    ? $"/maintenance/work-orders/{entry.WorkOrderId.Value:D}"
                    : null,
                session?.Permissions.Allows(
                    MaintenanceWorkPermissions.Manage(entry.ScopeId)) == true))
                .ToArray(),
            page.NextDueOn,
            page.NextObligationId?.Value));
    }

    private static async Task<IResult> QueryAssetsAsync(
        Guid scopeId,
        int pageSize,
        string? search,
        MaintenanceEquipmentLinkState? linkState,
        string? afterCode,
        Guid? afterAssetId,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceService maintenance,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        var result = await maintenance.QueryAssetsAsync(
            session,
            new MaintenanceAssetQuery(
                FacilityScopeId.From(scopeId),
                pageSize,
                search,
                linkState,
                afterCode,
                afterAssetId is null ? null : MaintenanceAssetId.From(afterAssetId.Value)),
            cancellationToken).ConfigureAwait(false);
        return ToHttpResult(result, page => new MaintenanceAssetPagePayload(
            page.Assets.Select(asset => ToPayload(asset, session)).ToArray(),
            page.NextCode,
            page.NextAssetId?.Value));
    }

    private static async Task<IResult> ReadAssetAsync(
        Guid assetId,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceService maintenance,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        return ToHttpResult(
            await maintenance.ReadAssetAsync(
                session,
                MaintenanceAssetId.From(assetId),
                cancellationToken).ConfigureAwait(false),
            asset => ToPayload(asset, session));
    }

    private static async Task<IResult> ReadLinkHistoryAsync(
        Guid assetId,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceService maintenance,
        CancellationToken cancellationToken)
    {
        var result = await maintenance.ReadLinkHistoryAsync(
            sessions.Resolve(context),
            MaintenanceAssetId.From(assetId),
            cancellationToken).ConfigureAwait(false);
        return ToHttpResult(result, history => history.Select(item => new MaintenanceLinkHistoryPayload(
            item.AssetVersion.Value,
            item.Action.ToString(),
            item.EquipmentId?.Value,
            item.ChangedAt)).ToArray());
    }

    private static async Task<IResult> CreateAssetAsync(
        CreateMaintenanceAssetInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceService maintenance,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        var result = await maintenance.CreateAssetAsync(
            session,
            new CreateMaintenanceAssetRequest(
                MaintenanceAssetId.From(input.AssetId),
                FacilityScopeId.From(input.ScopeId),
                input.Code,
                input.Name,
                input.IdempotencyKey),
            cancellationToken).ConfigureAwait(false);
        return ToHttpResult(result, value => ToCommandPayload(value, session));
    }

    private static async Task<IResult> UpdateAssetAsync(
        Guid assetId,
        UpdateMaintenanceAssetInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceService maintenance,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        var result = await maintenance.UpdateAssetAsync(
            session,
            new UpdateMaintenanceAssetRequest(
                MaintenanceAssetId.From(assetId),
                input.Code,
                input.Name,
                StateVersion.From(input.ExpectedVersion),
                input.IdempotencyKey),
            cancellationToken).ConfigureAwait(false);
        return ToHttpResult(result, value => ToCommandPayload(value, session));
    }

    private static async Task<IResult> LinkEquipmentAsync(
        Guid assetId,
        LinkMaintenanceEquipmentInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceService maintenance,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        var result = await maintenance.LinkEquipmentAsync(
            session,
            new LinkMaintenanceEquipmentRequest(
                MaintenanceAssetId.From(assetId),
                EquipmentId.From(input.EquipmentId),
                StateVersion.From(input.ExpectedVersion),
                input.IdempotencyKey),
            cancellationToken).ConfigureAwait(false);
        return ToHttpResult(result, value => ToCommandPayload(value, session));
    }

    private static async Task<IResult> ConfirmEquipmentLinkAsync(
        Guid assetId,
        VersionedMaintenanceAssetInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceService maintenance,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        var result = await maintenance.ConfirmEquipmentLinkAsync(
            session,
            new ConfirmMaintenanceEquipmentLinkRequest(
                MaintenanceAssetId.From(assetId),
                StateVersion.From(input.ExpectedVersion),
                input.IdempotencyKey),
            cancellationToken).ConfigureAwait(false);
        return ToHttpResult(result, value => ToCommandPayload(value, session));
    }

    private static async Task<IResult> UnlinkEquipmentAsync(
        Guid assetId,
        VersionedMaintenanceAssetInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceService maintenance,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        var result = await maintenance.UnlinkEquipmentAsync(
            session,
            new UnlinkMaintenanceEquipmentRequest(
                MaintenanceAssetId.From(assetId),
                StateVersion.From(input.ExpectedVersion),
                input.IdempotencyKey),
            cancellationToken).ConfigureAwait(false);
        return ToHttpResult(result, value => ToCommandPayload(value, session));
    }

    private static MaintenanceAssetCommandPayload ToCommandPayload(
        MaintenanceCommandResult result,
        SessionSnapshot? session) =>
        new(ToPayload(result.Asset, session), result.Disposition.ToString());

    private static MaintenanceAssetPayload ToPayload(
        MaintenanceAssetSnapshot asset,
        SessionSnapshot? session)
    {
        var canReadEquipment = session?.Permissions.Allows(
            EquipmentPermissions.Read(asset.ScopeId)) == true;
        return new MaintenanceAssetPayload(
            asset.AssetId.Value,
            asset.ScopeId.Value,
            asset.Code,
            asset.Name,
            canReadEquipment ? asset.EquipmentId?.Value : null,
            canReadEquipment && asset.EquipmentId is not null
                ? $"/equipment/{asset.EquipmentId.Value.Value:D}?scope={asset.ScopeId.Value:D}"
                : null,
            asset.EquipmentLinkState.ToString(),
            asset.Version.Value,
            session?.Permissions.Allows(MaintenancePermissions.ManageAsset(asset.ScopeId)) == true,
            asset.CreatedAt,
            asset.UpdatedAt);
    }

    private static IResult ToHttpResult<T, TPayload>(Result<T> result, Func<T, TPayload> map) =>
        result.IsSuccess ? Results.Ok(map(result.Value)) : Problem(result.Error!);

    private static IResult Problem(OperationError error) => Results.Problem(
        statusCode: error.Code.Value switch
        {
            "session.anonymous" or "session.expired" or "session.revoked" => StatusCodes.Status401Unauthorized,
            "permission.denied" => StatusCodes.Status403Forbidden,
            "maintenance.asset_not_found" or "maintenance.equipment_not_found" or
                "maintenance.plan_not_found" => StatusCodes.Status404NotFound,
            "maintenance.asset_query" or "maintenance.command_invalid" or
                "maintenance.asset_invalid" or "maintenance.equipment_link_state" or
                "maintenance.forecast_query" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status409Conflict,
        },
        title: error.Code.Value,
        detail: error.Message);
}

public sealed record CreateMaintenanceAssetInput(
    Guid AssetId,
    Guid ScopeId,
    string Code,
    string Name,
    string IdempotencyKey);

public sealed record UpdateMaintenanceAssetInput(
    string Code,
    string Name,
    ulong ExpectedVersion,
    string IdempotencyKey);

public sealed record LinkMaintenanceEquipmentInput(
    Guid EquipmentId,
    ulong ExpectedVersion,
    string IdempotencyKey);

public sealed record VersionedMaintenanceAssetInput(
    ulong ExpectedVersion,
    string IdempotencyKey);

public sealed record MaintenanceAssetPayload(
    Guid AssetId,
    Guid ScopeId,
    string Code,
    string Name,
    Guid? EquipmentId,
    string? EquipmentHref,
    string LinkState,
    ulong Version,
    bool CanManage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MaintenanceAssetPagePayload(
    IReadOnlyList<MaintenanceAssetPayload> Assets,
    string? NextCode,
    Guid? NextAssetId);

public sealed record MaintenanceLinkHistoryPayload(
    ulong AssetVersion,
    string Action,
    Guid? EquipmentId,
    DateTimeOffset ChangedAt);

public sealed record MaintenanceAssetCommandPayload(
    MaintenanceAssetPayload Asset,
    string Disposition);

public sealed record MaintenancePlanPayload(
    Guid PlanId,
    Guid AssetId,
    Guid ScopeId,
    ulong Revision,
    string Title,
    DateOnly FirstDueOn,
    int IntervalDays,
    DateOnly? EffectiveThrough,
    string AssetHref,
    bool CanMaterialize,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MaintenanceForecastPayload(
    Guid ObligationId,
    Guid WorkOrderId,
    Guid PlanId,
    Guid AssetId,
    Guid ScopeId,
    ulong PlanRevision,
    string Title,
    DateOnly DueOn,
    string State,
    bool IsOverdue,
    string? WorkOrderHref,
    bool CanManage);

public sealed record MaintenanceForecastPagePayload(
    IReadOnlyList<MaintenanceForecastPayload> Entries,
    DateOnly? NextDueOn,
    Guid? NextObligationId);
