using Dispatcher.Administration;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Dispatcher.Server;

public static class AdministrationEndpoints
{
    public static IServiceCollection AddAdministrationServer(
        this IServiceCollection services, string connectionString, string databaseRole,
        AdministrationQueryLimits limits)
    {
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(sp => new AdministrationStore(
            sp.GetRequiredService<NpgsqlDataSource>(), databaseRole, limits));
        return services;
    }

    public static IEndpointRouteBuilder MapAdministrationServer(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/administration/operations");
        group.MapGet("/health", ReadHealthAsync);
        group.MapGet("/data-quality", ReadDataQualityAsync);
        group.MapGet("/audit", ReadAuditAsync);
        return endpoints;
    }

    private static async Task<IResult> ReadHealthAsync(
        HttpContext context, RequestSessionResolver sessions, IWallClock clock,
        AdministrationStore store, NpgsqlDataSource dataSource, CancellationToken token)
    {
        var access = SessionAuthorization.AuthorizeAccess(
            sessions.Resolve(context), AdministrationPermissions.ReadHealth, clock);
        if (access.IsFailure) return Problem(access.Error!);
        var stored = await store.ReadHealthAsync(access.Value, token).ConfigureAwait(false);
        if (stored.IsFailure) return Problem(stored.Error!);
        var platform = await PlatformHealth.CheckAsync(dataSource, token).ConfigureAwait(false);
        var platformItem = new OperationalHealthItem(
            "MOD-PLT", null, OperationalHealthFacet.Persistence,
            platform.Readiness == ReadinessState.Ready
                ? OperationalHealthState.Healthy
                : OperationalHealthState.Unavailable,
            platform.ReasonCode ?? "ready", clock.GetUtcNow());
        var items = stored.Value.Items.Concat([platformItem]).ToArray();
        return Results.Ok(new OperationalHealthView(items,
            items.Count(value => value.State == OperationalHealthState.Healthy),
            items.Count(value => value.State == OperationalHealthState.Degraded),
            items.Count(value => value.State == OperationalHealthState.Unavailable),
            items.Count(value => value.State == OperationalHealthState.Unknown)));
    }

    private static async Task<IResult> ReadDataQualityAsync(
        HttpContext context, RequestSessionResolver sessions, IWallClock clock,
        AdministrationStore store, CancellationToken token)
    {
        var access = SessionAuthorization.AuthorizeAccess(
            sessions.Resolve(context), AdministrationPermissions.ReadDataQuality, clock);
        if (access.IsFailure) return Problem(access.Error!);
        var result = await store.ReadDataQualityAsync(access.Value, token).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);
    }

    private static async Task<IResult> ReadAuditAsync(
        string? cursor, int? pageSize, HttpContext context, RequestSessionResolver sessions,
        IWallClock clock, AdministrationStore store, CancellationToken token)
    {
        var access = SessionAuthorization.AuthorizeAccess(
            sessions.Resolve(context), AdministrationPermissions.ReadAudit, clock);
        if (access.IsFailure) return Problem(access.Error!);
        var result = await store.ReadAuditAsync(access.Value, new(cursor, pageSize ?? 50), token).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);
    }

    private static IResult Problem(OperationError error) => Results.Problem(
        statusCode: error.Code.Value switch
        {
            "session.anonymous" or "session.expired" or "session.revoked" => StatusCodes.Status401Unauthorized,
            "permission.denied" => StatusCodes.Status403Forbidden,
            "administration.view_overload" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        }, title: error.Code.Value, detail: error.Message);
}
