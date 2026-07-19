using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Dispatcher.Server;

public static class RegistryEndpoints
{
    public static IServiceCollection AddRegistryServer(
        this IServiceCollection services,
        string connectionString,
        string facilityDatabaseRole,
        string equipmentDatabaseRole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(facilityDatabaseRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(equipmentDatabaseRole);
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(sp => new FacilityStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            facilityDatabaseRole,
            sp.GetRequiredService<IWallClock>()));
        services.AddSingleton<FacilityService>();
        services.AddSingleton(sp => new EquipmentStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            equipmentDatabaseRole,
            sp.GetRequiredService<IWallClock>()));
        services.AddSingleton<EquipmentService>();
        services.AddSingleton<RegistryProjectionService>();
        return services;
    }

    public static IEndpointRouteBuilder MapRegistryServer(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/registry");
        group.MapGet("/scopes", async Task<IResult> (
            HttpContext context,
            RequestSessionResolver sessions,
            RegistryProjectionService registry,
            CancellationToken cancellationToken) =>
            ToHttpResult(await registry.ReadScopesAsync(
                sessions.Resolve(context),
                cancellationToken).ConfigureAwait(false)));

        group.MapGet("/locations", async Task<IResult> (
            Guid scopeId,
            string? query,
            HttpContext context,
            RequestSessionResolver sessions,
            RegistryProjectionService registry,
            CancellationToken cancellationToken) =>
            ToHttpResult(await registry.ReadLocationsAsync(
                sessions.Resolve(context),
                FacilityScopeId.From(scopeId),
                query ?? string.Empty,
                cancellationToken).ConfigureAwait(false)));

        group.MapGet("/locations/{locationId:guid}", async Task<IResult> (
            Guid locationId,
            Guid scopeId,
            HttpContext context,
            RequestSessionResolver sessions,
            RegistryProjectionService registry,
            CancellationToken cancellationToken) =>
            ToHttpResult(await registry.ReadLocationAsync(
                sessions.Resolve(context),
                FacilityScopeId.From(scopeId),
                LocationId.From(locationId),
                cancellationToken).ConfigureAwait(false)));

        group.MapGet("/locations/{locationId:guid}/plan-context", async Task<IResult> (
            Guid locationId,
            Guid scopeId,
            HttpContext context,
            RequestSessionResolver sessions,
            RegistryProjectionService registry,
            CancellationToken cancellationToken) =>
            ToHttpResult(await registry.ReadPlanContextAsync(
                sessions.Resolve(context),
                FacilityScopeId.From(scopeId),
                LocationId.From(locationId),
                cancellationToken).ConfigureAwait(false)));

        group.MapGet("/equipment", async Task<IResult> (
            Guid scopeId,
            string? query,
            HttpContext context,
            RequestSessionResolver sessions,
            RegistryProjectionService registry,
            CancellationToken cancellationToken) =>
            ToHttpResult(await registry.ReadEquipmentAsync(
                sessions.Resolve(context),
                FacilityScopeId.From(scopeId),
                query ?? string.Empty,
                cancellationToken).ConfigureAwait(false)));

        group.MapGet("/equipment/{equipmentId:guid}", async Task<IResult> (
            Guid equipmentId,
            Guid scopeId,
            HttpContext context,
            RequestSessionResolver sessions,
            RegistryProjectionService registry,
            CancellationToken cancellationToken) =>
            ToHttpResult(await registry.ReadEquipmentAsync(
                sessions.Resolve(context),
                FacilityScopeId.From(scopeId),
                EquipmentId.From(equipmentId),
                cancellationToken).ConfigureAwait(false)));
        return endpoints;
    }

    private static IResult ToHttpResult<TValue>(Result<TValue> result) => result.IsSuccess
        ? Results.Ok(result.Value)
        : Results.Problem(
            statusCode: StatusCode(result.Error!.Code.Value),
            title: result.Error.Code.Value,
            detail: result.Error.Message);

    private static int StatusCode(string code) => code switch
    {
        "session.anonymous" or "session.revoked" or "session.expired" => StatusCodes.Status401Unauthorized,
        "permission.denied" => StatusCodes.Status403Forbidden,
        "registry.location_not_found" or "registry.equipment_not_found" => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status400BadRequest,
    };
}
