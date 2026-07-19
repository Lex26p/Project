using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Workspace;

public sealed record WorkspaceNavigationItem(string Label, string Route);

public static class WorkspaceRoutes
{
    public const string Home = "/home";
    public const string Me = "/me";
    public const string Search = "/search";

    public static PermissionCode? RequiredPermission(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var route = path.Split('?', 2)[0].TrimEnd('/');
        if (route.Length == 0)
        {
            route = "/";
        }

        if (string.Equals(route, Home, StringComparison.OrdinalIgnoreCase))
        {
            return WorkspacePermissions.HomeRead;
        }

        if (string.Equals(route, Me, StringComparison.OrdinalIgnoreCase))
        {
            return WorkspacePermissions.ProfileSelfRead;
        }

        if (string.Equals(route, Search, StringComparison.OrdinalIgnoreCase))
        {
            return WorkspacePermissions.Search;
        }

        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2 &&
               string.Equals(segments[0], "users", StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParse(segments[1], out _)
            ? WorkspacePermissions.ProfileUserRead
            : null;
    }
}

public sealed class WorkspaceService
{
    private readonly WorkspaceStore store;
    private readonly IWallClock clock;

    public WorkspaceService(WorkspaceStore store, IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        this.store = store;
        this.clock = clock;
    }

    public Result<IReadOnlyList<WorkspaceNavigationItem>> ReadNavigation(SessionSnapshot? session)
    {
        var sessionValidation = SessionAuthorization.ValidateSession(session, clock);
        if (sessionValidation.IsFailure)
        {
            return Result.Failure<IReadOnlyList<WorkspaceNavigationItem>>(sessionValidation.Error!);
        }

        var items = new List<WorkspaceNavigationItem>();
        AddNavigation(items, session, WorkspacePermissions.HomeRead, "Home", WorkspaceRoutes.Home);
        AddNavigation(items, session, WorkspacePermissions.ProfileSelfRead, "My profile", WorkspaceRoutes.Me);
        AddNavigation(items, session, WorkspacePermissions.Search, "Search", WorkspaceRoutes.Search);
        return items.Count == 0
            ? Failure<IReadOnlyList<WorkspaceNavigationItem>>("permission.denied", "No workspace route is accessible.")
            : Result.Success<IReadOnlyList<WorkspaceNavigationItem>>(items);
    }

    public Result<bool> CanAccess(SessionSnapshot? session, string route)
    {
        var permission = WorkspaceRoutes.RequiredPermission(route);
        if (permission is null)
        {
            return Failure<bool>("workspace.route_not_found", "The route is not a canonical workspace route.");
        }

        var authorization = SessionAuthorization.AuthorizeAccess(session, permission, clock);
        return authorization.IsSuccess
            ? Result.Success(true)
            : Result.Failure<bool>(authorization.Error!);
    }

    public async Task<Result<WorkspaceHome>> ReadHomeAsync(
        SessionSnapshot? session,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(session, WorkspacePermissions.HomeRead, cancellationToken).ConfigureAwait(false);
        if (context.IsFailure)
        {
            return Result.Failure<WorkspaceHome>(context.Error!);
        }

        var profile = await store.ReadSelfProfileAsync(context.Value.Account.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return Failure<WorkspaceHome>("workspace.person_not_found", "The account person was not found.");
        }

        var preferences = await store.ReadPreferencesAsync(context.Value.Account.AccountId, cancellationToken)
            .ConfigureAwait(false);
        var candidates = await store.ReadHomeItemsAsync(context.Value.Account.AccountId, cancellationToken)
            .ConfigureAwait(false);
        var items = FilterItems(candidates, context.Value.Authorization.Session.Permissions);
        return Result.Success(new WorkspaceHome(profile, preferences, items));
    }

    public async Task<Result<PersonProfile>> ReadMeAsync(
        SessionSnapshot? session,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(session, WorkspacePermissions.ProfileSelfRead, cancellationToken)
            .ConfigureAwait(false);
        if (context.IsFailure)
        {
            return Result.Failure<PersonProfile>(context.Error!);
        }

        var profile = await store.ReadSelfProfileAsync(context.Value.Account.AccountId, cancellationToken)
            .ConfigureAwait(false);
        return profile is null
            ? Failure<PersonProfile>("workspace.person_not_found", "The account person was not found.")
            : Result.Success(profile);
    }

    public async Task<Result<PersonProfile>> ReadUserAsync(
        SessionSnapshot? session,
        AccountId targetAccountId,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(session, WorkspacePermissions.ProfileUserRead, cancellationToken)
            .ConfigureAwait(false);
        if (context.IsFailure)
        {
            return Result.Failure<PersonProfile>(context.Error!);
        }

        var profile = await store.ReadVisibleProfileAsync(
            context.Value.Account.AccountId,
            targetAccountId,
            cancellationToken).ConfigureAwait(false);
        return profile is null
            ? Failure<PersonProfile>("workspace.profile_not_found", "The profile is not visible.")
            : Result.Success(profile);
    }

    public async Task<Result<IReadOnlyList<WorkspaceSearchResult>>> SearchAsync(
        SessionSnapshot? session,
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Result.Success<IReadOnlyList<WorkspaceSearchResult>>([]);
        }

        var context = await ResolveAsync(session, WorkspacePermissions.Search, cancellationToken).ConfigureAwait(false);
        if (context.IsFailure)
        {
            return Result.Failure<IReadOnlyList<WorkspaceSearchResult>>(context.Error!);
        }

        var candidates = await store.SearchAsync(context.Value.Account.AccountId, query, cancellationToken)
            .ConfigureAwait(false);
        var results = candidates
            .Where(item => context.Value.Authorization.Session.Permissions.Allows(
                PermissionCode.From(item.RequiredPermission)))
            .ToArray();
        return Result.Success<IReadOnlyList<WorkspaceSearchResult>>(results);
    }

    public Task<Result<IReadOnlyList<HomeItem>>> ReadFavoritesAsync(
        SessionSnapshot? session,
        CancellationToken cancellationToken = default) =>
        ReadTrackedAsync(session, true, cancellationToken);

    public Task<Result<IReadOnlyList<HomeItem>>> ReadRecentAsync(
        SessionSnapshot? session,
        CancellationToken cancellationToken = default) =>
        ReadTrackedAsync(session, false, cancellationToken);

    public async Task<Result> UpdateProfileAsync(
        SessionSnapshot? session,
        ProfileSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.DisplayName) ||
            !Enum.IsDefined(settings.Availability) ||
            !Enum.IsDefined(settings.Visibility))
        {
            return Failure("workspace.profile_invalid", "Profile settings are invalid.");
        }

        var context = await ResolveAsync(session, WorkspacePermissions.ProfileSelfWrite, cancellationToken)
            .ConfigureAwait(false);
        if (context.IsFailure)
        {
            return Result.Failure(context.Error!);
        }

        await store.UpdateProfileAsync(
            context.Value.Authorization,
            context.Value.Account.AccountId,
            settings,
            cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result> UpdatePreferencesAsync(
        SessionSnapshot? session,
        WorkspacePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(session, WorkspacePermissions.PreferencesWrite, cancellationToken)
            .ConfigureAwait(false);
        if (context.IsFailure)
        {
            return Result.Failure(context.Error!);
        }

        var routePermission = WorkspaceRoutes.RequiredPermission(preferences.StartRoute);
        if (routePermission is null ||
            !context.Value.Authorization.Session.Permissions.Allows(routePermission))
        {
            return Failure("workspace.start_route_denied", "The start route is not an accessible workspace route.");
        }

        await store.UpdatePreferencesAsync(
            context.Value.Authorization,
            context.Value.Account.AccountId,
            preferences,
            cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result> SetHomeOverrideAsync(
        SessionSnapshot? session,
        HomeItemId itemId,
        bool hidden,
        int? order,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(session, WorkspacePermissions.HomeCustomize, cancellationToken)
            .ConfigureAwait(false);
        if (context.IsFailure)
        {
            return Result.Failure(context.Error!);
        }

        var item = await FindAccessibleItemAsync(context.Value, itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Failure("workspace.item_not_found", "The home item is not accessible.");
        }

        await store.SetHomeOverrideAsync(
            context.Value.Authorization,
            context.Value.Account.AccountId,
            itemId,
            hidden,
            order,
            cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result> SetFavoriteAsync(
        SessionSnapshot? session,
        HomeItemId itemId,
        bool favorite,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(session, WorkspacePermissions.FavoritesWrite, cancellationToken)
            .ConfigureAwait(false);
        if (context.IsFailure)
        {
            return Result.Failure(context.Error!);
        }

        var item = await FindAccessibleItemAsync(context.Value, itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Failure("workspace.item_not_found", "The home item is not accessible.");
        }

        await store.SetFavoriteAsync(
            context.Value.Authorization,
            context.Value.Account.AccountId,
            itemId,
            favorite,
            cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result> RecordRecentAsync(
        SessionSnapshot? session,
        HomeItemId itemId,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(session, WorkspacePermissions.RecentsWrite, cancellationToken)
            .ConfigureAwait(false);
        if (context.IsFailure)
        {
            return Result.Failure(context.Error!);
        }

        var item = await FindAccessibleItemAsync(context.Value, itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Failure("workspace.item_not_found", "The home item is not accessible.");
        }

        await store.RecordRecentAsync(
            context.Value.Authorization,
            context.Value.Account.AccountId,
            itemId,
            cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<Result<IReadOnlyList<HomeItem>>> ReadTrackedAsync(
        SessionSnapshot? session,
        bool favorites,
        CancellationToken cancellationToken)
    {
        var context = await ResolveAsync(session, WorkspacePermissions.HomeRead, cancellationToken).ConfigureAwait(false);
        if (context.IsFailure)
        {
            return Result.Failure<IReadOnlyList<HomeItem>>(context.Error!);
        }

        var assigned = await store.ReadHomeItemsAsync(context.Value.Account.AccountId, cancellationToken)
            .ConfigureAwait(false);
        var assignedIds = assigned.Select(item => item.ItemId).ToHashSet();
        var tracked = favorites
            ? await store.ReadFavoritesAsync(context.Value.Account.AccountId, cancellationToken).ConfigureAwait(false)
            : await store.ReadRecentAsync(context.Value.Account.AccountId, cancellationToken).ConfigureAwait(false);
        var filtered = FilterItems(tracked, context.Value.Authorization.Session.Permissions)
            .Where(item => assignedIds.Contains(item.ItemId))
            .ToArray();
        return Result.Success<IReadOnlyList<HomeItem>>(filtered);
    }

    private async Task<HomeItem?> FindAccessibleItemAsync(
        WorkspaceContext context,
        HomeItemId itemId,
        CancellationToken cancellationToken)
    {
        var items = await store.ReadHomeItemsIncludingHiddenAsync(
            context.Account.AccountId,
            cancellationToken)
            .ConfigureAwait(false);
        return items.SingleOrDefault(item =>
            item.ItemId == itemId &&
            context.Authorization.Session.Permissions.Allows(PermissionCode.From(item.RequiredPermission)));
    }

    private async Task<Result<WorkspaceContext>> ResolveAsync(
        SessionSnapshot? session,
        PermissionCode permission,
        CancellationToken cancellationToken)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(session, permission, clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<WorkspaceContext>(authorization.Error!);
        }

        var account = await store.FindAccountAsync(authorization.Value.Session.SubjectId, cancellationToken)
            .ConfigureAwait(false);
        return account is null
            ? Failure<WorkspaceContext>("workspace.account_not_found", "No workspace account is linked to the session.")
            : Result.Success(new WorkspaceContext(authorization.Value, account));
    }

    private void AddNavigation(
        List<WorkspaceNavigationItem> items,
        SessionSnapshot? session,
        PermissionCode permission,
        string label,
        string route)
    {
        if (SessionAuthorization.AuthorizeAccess(session, permission, clock).IsSuccess)
        {
            items.Add(new WorkspaceNavigationItem(label, route));
        }
    }

    private static HomeItem[] FilterItems(
        IEnumerable<HomeItem> items,
        EffectivePermissions permissions) =>
        items.Where(item => permissions.Allows(PermissionCode.From(item.RequiredPermission))).ToArray();

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));

    private sealed record WorkspaceContext(AuthorizedAccess Authorization, WorkspaceAccount Account);
}
