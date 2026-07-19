using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Dispatcher.Server;

public static class WorkspaceEndpoints
{
    public static IServiceCollection AddWorkspaceServer(
        this IServiceCollection services,
        string connectionString,
        string databaseRole)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(sp => new WorkspaceStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            databaseRole,
            sp.GetRequiredService<IWallClock>()));
        services.AddSingleton<WorkspaceService>();
        return services;
    }

    public static IEndpointRouteBuilder MapWorkspaceServer(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/workspace");

        group.MapGet("/access", (
            string route,
            HttpContext context,
            RequestSessionResolver sessions,
            WorkspaceService workspace) =>
        {
            var session = sessions.Resolve(context);
            var registry = context.RequestServices.GetService<RegistryProjectionService>();
            if (registry is not null && RegistryRoutes.IsCanonical(route))
            {
                return ToHttpResult(registry.CanAccess(session, route));
            }

            if (string.Equals(route.Split('?', 2)[0], "/current", StringComparison.OrdinalIgnoreCase))
            {
                var access = SessionAuthorization.AuthorizeAccess(
                    session,
                    RuntimePermissions.ReadCurrent,
                    SystemClock.Instance);
                return ToHttpResult(access.IsSuccess
                    ? Result.Success(true)
                    : Result.Failure<bool>(access.Error!));
            }

            return ToHttpResult(workspace.CanAccess(session, route));
        });

        group.MapGet("/navigation", (
            HttpContext context,
            RequestSessionResolver sessions,
            WorkspaceService workspace) =>
        {
            var session = sessions.Resolve(context);
            var result = workspace.ReadNavigation(session);
            if (result.IsFailure)
            {
                return ToHttpResult(result);
            }

            var items = result.Value
                .Select(item => new WorkspaceNavigationPayload(item.Label, item.Route))
                .ToList();
            if (session is not null && session.Permissions.Allows(RuntimePermissions.ReadCurrent))
            {
                items.Add(new WorkspaceNavigationPayload("Current", "/current"));
            }

            var registry = context.RequestServices.GetService<RegistryProjectionService>();
            if (registry?.ReadableScopes(session) is { IsSuccess: true } scopes && scopes.Value.Count > 0)
            {
                items.Add(new WorkspaceNavigationPayload("Locations", RegistryRoutes.Locations));
                items.Add(new WorkspaceNavigationPayload("Equipment", RegistryRoutes.Equipment));
            }

            return Results.Ok(items);
        });

        group.MapGet("/home", async Task<IResult> (
            HttpContext context,
            RequestSessionResolver sessions,
            WorkspaceService workspace,
            CancellationToken cancellationToken) =>
        {
            var session = sessions.Resolve(context);
            var home = await workspace.ReadHomeAsync(session, cancellationToken).ConfigureAwait(false);
            if (home.IsFailure)
            {
                return ToHttpResult(home);
            }

            var favorites = await workspace.ReadFavoritesAsync(session, cancellationToken).ConfigureAwait(false);
            var recent = await workspace.ReadRecentAsync(session, cancellationToken).ConfigureAwait(false);
            if (favorites.IsFailure || recent.IsFailure)
            {
                return ToHttpResult(favorites.IsFailure ? favorites : recent);
            }

            return Results.Ok(new WorkspaceHomePayload(
                ToPayload(home.Value.Profile),
                new WorkspacePreferencesPayload(
                    home.Value.Preferences.CompactNavigation,
                    home.Value.Preferences.StartRoute),
                home.Value.Items.Select(ToPayload).ToArray(),
                favorites.Value.Select(ToPayload).ToArray(),
                recent.Value.Select(ToPayload).ToArray()));
        });

        group.MapGet("/me", async Task<IResult> (
            HttpContext context,
            RequestSessionResolver sessions,
            WorkspaceService workspace,
            CancellationToken cancellationToken) =>
            ToHttpResult(await workspace.ReadMeAsync(
                sessions.Resolve(context),
                cancellationToken).ConfigureAwait(false),
                ToPayload));

        group.MapGet("/users/{userId:guid}", async Task<IResult> (
            Guid userId,
            HttpContext context,
            RequestSessionResolver sessions,
            WorkspaceService workspace,
            CancellationToken cancellationToken) =>
            ToHttpResult(await workspace.ReadUserAsync(
                sessions.Resolve(context),
                AccountId.From(userId),
                cancellationToken).ConfigureAwait(false),
                ToPayload));

        group.MapGet("/search", async Task<IResult> (
            string? query,
            HttpContext context,
            RequestSessionResolver sessions,
            WorkspaceService workspace,
            CancellationToken cancellationToken) =>
        {
            var result = await workspace.SearchAsync(
                sessions.Resolve(context),
                query ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? Results.Ok(result.Value.Select(item => new WorkspaceSearchPayload(
                    item.Kind,
                    item.Id,
                    item.Label,
                    item.Route)))
                : ToHttpResult(result);
        });

        group.MapPut("/me/profile", async Task<IResult> (
            UpdateProfileRequest request,
            HttpContext context,
            RequestSessionResolver sessions,
            WorkspaceService workspace,
            CancellationToken cancellationToken) =>
            ToHttpResult(await workspace.UpdateProfileAsync(
                sessions.Resolve(context),
                new ProfileSettings(
                    request.DisplayName,
                    request.Title,
                    (PersonAvailability)request.Availability,
                    (ProfileVisibility)request.Visibility),
                cancellationToken).ConfigureAwait(false)));

        group.MapPut("/me/preferences", async Task<IResult> (
            UpdatePreferencesRequest request,
            HttpContext context,
            RequestSessionResolver sessions,
            WorkspaceService workspace,
            CancellationToken cancellationToken) =>
            ToHttpResult(await workspace.UpdatePreferencesAsync(
                sessions.Resolve(context),
                new WorkspacePreferences(request.CompactNavigation, request.StartRoute),
                cancellationToken).ConfigureAwait(false)));

        group.MapPut("/home/items/{itemId:guid}", async Task<IResult> (
            Guid itemId,
            UpdateHomeOverrideRequest request,
            HttpContext context,
            RequestSessionResolver sessions,
            WorkspaceService workspace,
            CancellationToken cancellationToken) =>
            ToHttpResult(await workspace.SetHomeOverrideAsync(
                sessions.Resolve(context),
                HomeItemId.From(itemId),
                request.Hidden,
                request.Order,
                cancellationToken).ConfigureAwait(false)));

        group.MapPut("/favorites/{itemId:guid}", async Task<IResult> (
            Guid itemId,
            UpdateFavoriteRequest request,
            HttpContext context,
            RequestSessionResolver sessions,
            WorkspaceService workspace,
            CancellationToken cancellationToken) =>
            ToHttpResult(await workspace.SetFavoriteAsync(
                sessions.Resolve(context),
                HomeItemId.From(itemId),
                request.Favorite,
                cancellationToken).ConfigureAwait(false)));

        group.MapPost("/recent/{itemId:guid}", async Task<IResult> (
            Guid itemId,
            HttpContext context,
            RequestSessionResolver sessions,
            WorkspaceService workspace,
            CancellationToken cancellationToken) =>
            ToHttpResult(await workspace.RecordRecentAsync(
                sessions.Resolve(context),
                HomeItemId.From(itemId),
                cancellationToken).ConfigureAwait(false)));

        return endpoints;
    }

    private static PersonProfilePayload ToPayload(PersonProfile profile) => new(
        profile.AccountId.Value,
        profile.PersonId.Value,
        profile.DisplayName,
        profile.Title,
        profile.Availability.ToString(),
        profile.Visibility.ToString());

    private static HomeItemPayload ToPayload(HomeItem item) => new(
        item.ItemId.Value,
        item.Label,
        item.Route,
        item.Order);

    private static IResult ToHttpResult(Result result) => result.IsSuccess
        ? Results.NoContent()
        : Problem(result.Error!);

    private static IResult ToHttpResult<TValue>(Result<TValue> result) => result.IsSuccess
        ? Results.Ok(result.Value)
        : Problem(result.Error!);

    private static IResult ToHttpResult<TValue, TPayload>(
        Result<TValue> result,
        Func<TValue, TPayload> map) => result.IsSuccess
        ? Results.Ok(map(result.Value))
        : Problem(result.Error!);

    private static IResult Problem(OperationError error) => Results.Problem(
        statusCode: StatusCode(error.Code.Value),
        title: error.Code.Value,
        detail: error.Message);

    private static int StatusCode(string code) => code switch
    {
        "session.anonymous" or "session.revoked" or "session.expired" => StatusCodes.Status401Unauthorized,
        "permission.denied" => StatusCodes.Status403Forbidden,
        "workspace.route_not_found" or "workspace.profile_not_found" or
            "workspace.account_not_found" or "workspace.person_not_found" or
            "workspace.item_not_found" => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status400BadRequest,
    };
}
