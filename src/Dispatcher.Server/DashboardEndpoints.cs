using Dispatcher.Dashboards;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Dispatcher.Server;

public sealed class AuthorizedDashboardService
{
    private readonly DashboardStore store;
    private readonly IWallClock clock;

    public AuthorizedDashboardService(DashboardStore store, IWallClock clock)
    {
        this.store = store;
        this.clock = clock;
    }

    public async Task<Result<IReadOnlyList<DashboardCatalogItem>>> ReadCatalogAsync(
        SessionSnapshot? session,
        CancellationToken cancellationToken)
    {
        var access = SessionAuthorization.AuthorizeAccess(session, DashboardPermissions.CatalogRead, clock);
        if (access.IsFailure)
        {
            return Result.Failure<IReadOnlyList<DashboardCatalogItem>>(access.Error!);
        }

        var catalog = await store.ReadCatalogAsync(access.Value.Session.SubjectId, cancellationToken)
            .ConfigureAwait(false);
        return Result.Success<IReadOnlyList<DashboardCatalogItem>>(catalog
            .Where(item => access.Value.Session.Permissions.Allows(DashboardPermissions.Read(item.DashboardId)))
            .ToArray());
    }

    public async Task<Result<DashboardRevision>> ReadManifestAsync(
        SessionSnapshot? session,
        DashboardId dashboardId,
        CancellationToken cancellationToken)
    {
        var access = SessionAuthorization.AuthorizeAccess(session, DashboardPermissions.Read(dashboardId), clock);
        if (access.IsFailure)
        {
            return Result.Failure<DashboardRevision>(access.Error!);
        }

        var published = await store.ReadPublishedAsync(dashboardId, cancellationToken).ConfigureAwait(false);
        if (published is null)
        {
            return Failure<DashboardRevision>("dashboard.not_found", "The published Dashboard was not found.");
        }

        var allowedBindings = published.Revision.Windows
            .SelectMany(window => window.Bindings)
            .Where(binding => access.Value.Session.Permissions.Allows(binding.RequiredPermission) &&
                              CanReadSource(access.Value.Session.Permissions, binding.Source))
            .Select(binding => binding.BindingId)
            .ToHashSet();
        var windows = published.Revision.Windows.Select(window =>
        {
            var bindings = window.Bindings.Where(binding => allowedBindings.Contains(binding.BindingId)).ToArray();
            var widgets = window.Widgets
                .Select(widget => widget with
                {
                    BindingIds = widget.BindingIds.Where(allowedBindings.Contains).ToArray(),
                })
                .Where(widget => widget.BindingIds.Count > 0)
                .ToArray();
            return window with { Widgets = widgets, Bindings = bindings };
        }).ToArray();
        var dependencies = published.Revision.Dependencies
            .Where(item => allowedBindings.Contains(item.BindingId))
            .ToArray();
        return Result.Success(published.Revision with { Windows = windows, Dependencies = dependencies });
    }

    private static bool CanReadSource(EffectivePermissions permissions, DashboardBindingSource source) =>
        source switch
        {
            DashboardBindingSource.Current => permissions.Allows(RuntimePermissions.ReadCurrent),
            DashboardBindingSource.Alarm => permissions.Allows(EventPermissions.ReadDispatcher),
            DashboardBindingSource.History => permissions.Allows(HistoryPermissions.ReadRange),
            _ => false,
        };

    public async Task<Result<DashboardId?>> ResolveLandingAsync(
        SessionSnapshot? session,
        CancellationToken cancellationToken)
    {
        var catalog = await ReadCatalogAsync(session, cancellationToken).ConfigureAwait(false);
        if (catalog.IsFailure)
        {
            return Result.Failure<DashboardId?>(catalog.Error!);
        }

        var subjectId = session!.SubjectId;
        var last = await store.ReadLastDashboardAsync(subjectId, cancellationToken).ConfigureAwait(false);
        var selected = last is not null && catalog.Value.Any(item => item.DashboardId == last.Value)
            ? last
            : catalog.Value.Count > 0 ? catalog.Value[0].DashboardId : null;
        if (selected is not null && selected != last)
        {
            await store.RecordOpenedAsync(subjectId, selected.Value, cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(selected);
    }

    public async Task<Result> SetFavoriteAsync(
        SessionSnapshot? session,
        DashboardId dashboardId,
        bool favorite,
        CancellationToken cancellationToken)
    {
        var access = AuthorizePersonal(session, dashboardId);
        if (access.IsFailure)
        {
            return Result.Failure(access.Error!);
        }

        await store.SetFavoriteAsync(access.Value.Session.SubjectId, dashboardId, favorite, cancellationToken)
            .ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result> RecordOpenedAsync(
        SessionSnapshot? session,
        DashboardId dashboardId,
        CancellationToken cancellationToken)
    {
        var access = AuthorizePersonal(session, dashboardId);
        if (access.IsFailure)
        {
            return Result.Failure(access.Error!);
        }

        await store.RecordOpenedAsync(access.Value.Session.SubjectId, dashboardId, cancellationToken)
            .ConfigureAwait(false);
        return Result.Success();
    }

    private Result<AuthorizedAccess> AuthorizePersonal(SessionSnapshot? session, DashboardId dashboardId)
    {
        var personal = SessionAuthorization.AuthorizeAccess(session, DashboardPermissions.Personalize, clock);
        if (personal.IsFailure)
        {
            return personal;
        }

        return personal.Value.Session.Permissions.Allows(DashboardPermissions.Read(dashboardId))
            ? personal
            : Result.Failure<AuthorizedAccess>(new OperationError(
                ErrorCode.From("permission.denied"), "Dashboard access is denied."));
    }

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));
}

public static class DashboardEndpoints
{
    public static IServiceCollection AddDashboardServer(
        this IServiceCollection services,
        string connectionString,
        string databaseRole,
        DashboardRuntimeLimits runtimeLimits,
        SvgIntakeLimits svgIntakeLimits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        ArgumentNullException.ThrowIfNull(runtimeLimits);
        ArgumentNullException.ThrowIfNull(svgIntakeLimits);
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(sp => new DashboardStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            databaseRole,
            sp.GetRequiredService<IWallClock>()));
        services.AddSingleton<AuthorizedDashboardService>();
        services.AddSingleton(runtimeLimits);
        services.AddSingleton<DashboardSubscriptionGenerationStore>();
        services.AddSingleton<DashboardSubscriptionService>();
        services.AddSingleton(svgIntakeLimits);
        services.AddSingleton<DashboardAuthoringService>();
        return services;
    }

    public static IEndpointRouteBuilder MapDashboardServer(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/dashboards");
        group.MapGet("/", ReadCatalogAsync);
        group.MapGet("/landing", ResolveLandingAsync);
        group.MapGet("/{dashboardId:guid}", ReadManifestAsync);
        group.MapPost("/{dashboardId:guid}/subscriptions", CreateSubscriptionAsync);
        group.MapGet("/subscriptions/{subscriptionId:guid}/status", ReadSubscriptionStatus);
        group.MapPut("/{dashboardId:guid}/favorite", SetFavoriteAsync);
        group.MapPost("/{dashboardId:guid}/opened", RecordOpenedAsync);
        return endpoints;
    }

    private static async Task<IResult> ReadCatalogAsync(
        HttpContext context,
        RequestSessionResolver sessions,
        AuthorizedDashboardService dashboards,
        CancellationToken cancellationToken)
    {
        var result = await dashboards.ReadCatalogAsync(sessions.Resolve(context), cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Ok(result.Value.Select(item => new DashboardCatalogPayload(
                item.DashboardId.Value, item.Name, item.Description, item.IsFavorite, item.LastOpenedAt)))
            : Problem(result.Error!);
    }

    private static async Task<IResult> ResolveLandingAsync(
        HttpContext context,
        RequestSessionResolver sessions,
        AuthorizedDashboardService dashboards,
        CancellationToken cancellationToken)
    {
        var result = await dashboards.ResolveLandingAsync(sessions.Resolve(context), cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(new DashboardLandingPayload(result.Value?.Value)) : Problem(result.Error!);
    }

    private static async Task<IResult> ReadManifestAsync(
        Guid dashboardId,
        HttpContext context,
        RequestSessionResolver sessions,
        AuthorizedDashboardService dashboards,
        CancellationToken cancellationToken)
    {
        var result = await dashboards.ReadManifestAsync(
            sessions.Resolve(context), DashboardId.From(dashboardId), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(ToPayload(result.Value)) : Problem(result.Error!);
    }

    private static async Task<IResult> SetFavoriteAsync(
        Guid dashboardId,
        DashboardFavoriteRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        AuthorizedDashboardService dashboards,
        CancellationToken cancellationToken)
    {
        var result = await dashboards.SetFavoriteAsync(
            sessions.Resolve(context), DashboardId.From(dashboardId), request.Favorite, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? Results.NoContent() : Problem(result.Error!);
    }

    private static IResult ReadSubscriptionStatus(
        Guid subscriptionId,
        HttpContext context,
        RequestSessionResolver sessions,
        DashboardSubscriptionService subscriptions) =>
        Results.Ok(new DashboardSubscriptionStatusPayload(
            subscriptions.IsCurrent(sessions.Resolve(context), subscriptionId)));

    private static async Task<IResult> CreateSubscriptionAsync(
        Guid dashboardId,
        DashboardSubscriptionRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        DashboardSubscriptionService subscriptions,
        CancellationToken cancellationToken)
    {
        var result = await subscriptions.CreateAsync(
            sessions.Resolve(context),
            DashboardId.From(dashboardId),
            request.VisibleWindowIds.Select(DashboardWindowId.From).ToArray(),
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);
    }

    private static async Task<IResult> RecordOpenedAsync(
        Guid dashboardId,
        HttpContext context,
        RequestSessionResolver sessions,
        AuthorizedDashboardService dashboards,
        CancellationToken cancellationToken)
    {
        var result = await dashboards.RecordOpenedAsync(
            sessions.Resolve(context), DashboardId.From(dashboardId), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.NoContent() : Problem(result.Error!);
    }

    private static DashboardManifestPayload ToPayload(DashboardRevision revision) => new(
        revision.DashboardId.Value,
        revision.RevisionId.Value,
        revision.RevisionNumber,
        revision.Name,
        revision.Description,
        revision.Windows.Select(window => new DashboardWindowPayload(
            window.WindowId.Value,
            window.Title,
            window.Widgets.Select(widget => new DashboardWidgetPayload(
                widget.WidgetId.Value, widget.Kind, widget.Title,
                widget.BindingIds.Select(id => id.Value).ToArray())).ToArray(),
            window.Bindings.Select(binding => new DashboardBindingPayload(
                binding.BindingId.Value, binding.Source.ToString(), binding.ScopeId.Value, binding.PointId.Value))
                .ToArray())).ToArray(),
        revision.Dependencies.Select(item => new DashboardDependencyPayload(
            item.BindingId.Value, item.Key, item.Fingerprint)).ToArray(),
        revision.PublishedAt);

    private static IResult Problem(OperationError error) => Results.Problem(
        statusCode: error.Code.Value switch
        {
            "session.anonymous" or "session.revoked" or "session.expired" => StatusCodes.Status401Unauthorized,
            "permission.denied" => StatusCodes.Status403Forbidden,
            "dashboard.not_found" => StatusCodes.Status404NotFound,
            "dashboard.window_not_found" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        },
        title: error.Code.Value,
        detail: error.Message);
}

public sealed record DashboardFavoriteRequest(bool Favorite);
public sealed record DashboardSubscriptionRequest(IReadOnlyList<Guid> VisibleWindowIds);
public sealed record DashboardSubscriptionStatusPayload(bool IsCurrent);
public sealed record DashboardCatalogPayload(
    Guid DashboardId, string Name, string? Description, bool IsFavorite, DateTimeOffset? LastOpenedAt);
public sealed record DashboardLandingPayload(Guid? DashboardId);
public sealed record DashboardManifestPayload(
    Guid DashboardId, Guid RevisionId, ulong RevisionNumber, string Name, string? Description,
    IReadOnlyList<DashboardWindowPayload> Windows,
    IReadOnlyList<DashboardDependencyPayload> Dependencies,
    DateTimeOffset PublishedAt);
public sealed record DashboardWindowPayload(
    Guid WindowId, string Title, IReadOnlyList<DashboardWidgetPayload> Widgets,
    IReadOnlyList<DashboardBindingPayload> Bindings);
public sealed record DashboardWidgetPayload(
    Guid WidgetId, string Kind, string Title, IReadOnlyList<Guid> BindingIds);
public sealed record DashboardBindingPayload(Guid BindingId, string Source, Guid ScopeId, Guid PointId);
public sealed record DashboardDependencyPayload(Guid BindingId, string Key, string Fingerprint);
