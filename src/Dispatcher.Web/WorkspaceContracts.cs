namespace Dispatcher.Web;

public sealed record WorkspaceNavigationPayload(string Label, string Route);

public sealed record PersonProfilePayload(
    Guid UserId,
    Guid PersonId,
    string DisplayName,
    string? Title,
    string Availability,
    string Visibility);

public sealed record WorkspacePreferencesPayload(bool CompactNavigation, string StartRoute);

public sealed record HomeItemPayload(Guid ItemId, string Label, string Route, int Order);

public sealed record WorkspaceHomePayload(
    PersonProfilePayload Profile,
    WorkspacePreferencesPayload Preferences,
    IReadOnlyList<HomeItemPayload> Items,
    IReadOnlyList<HomeItemPayload> Favorites,
    IReadOnlyList<HomeItemPayload> Recent);

public sealed record WorkspaceSearchPayload(string Kind, Guid Id, string Label, string Route);

public sealed record UpdateProfileRequest(
    string DisplayName,
    string? Title,
    int Availability,
    int Visibility);

public sealed record UpdatePreferencesRequest(bool CompactNavigation, string StartRoute);

public sealed record UpdateHomeOverrideRequest(bool Hidden, int? Order);

public sealed record UpdateFavoriteRequest(bool Favorite);
