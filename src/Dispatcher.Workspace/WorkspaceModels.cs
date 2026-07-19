using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Workspace;

public readonly record struct AccountId
{
    private readonly CanonicalId<AccountId> value;

    private AccountId(CanonicalId<AccountId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static AccountId From(Guid value) => new(CanonicalId.From<AccountId>(value));
}

public readonly record struct PersonId
{
    private readonly CanonicalId<PersonId> value;

    private PersonId(CanonicalId<PersonId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static PersonId From(Guid value) => new(CanonicalId.From<PersonId>(value));
}

public readonly record struct HomeItemId
{
    private readonly CanonicalId<HomeItemId> value;

    private HomeItemId(CanonicalId<HomeItemId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static HomeItemId From(Guid value) => new(CanonicalId.From<HomeItemId>(value));
}

public enum WorkspaceAudienceKind
{
    Account = 1,
    Role = 2,
    Organization = 3,
}

public enum PersonAvailability
{
    Available = 1,
    Away = 2,
    Unavailable = 3,
}

public enum ProfileVisibility
{
    Organization = 1,
    Private = 2,
}

public sealed record WorkspaceAccount(AccountId AccountId, SubjectId SubjectId, PersonId PersonId);

public sealed record PersonProfile(
    AccountId AccountId,
    PersonId PersonId,
    string DisplayName,
    string? Title,
    PersonAvailability Availability,
    ProfileVisibility Visibility);

public sealed record WorkspacePreferences(bool CompactNavigation, string StartRoute);

public sealed record HomeItem(
    HomeItemId ItemId,
    string Label,
    string Route,
    string RequiredPermission,
    int Order);

public sealed record WorkspaceHome(
    PersonProfile Profile,
    WorkspacePreferences Preferences,
    IReadOnlyList<HomeItem> Items);

public sealed record WorkspaceSearchResult(
    string Kind,
    Guid Id,
    string Label,
    string Route,
    string RequiredPermission);

public sealed record ProfileSettings(
    string DisplayName,
    string? Title,
    PersonAvailability Availability,
    ProfileVisibility Visibility);

public static class WorkspacePermissions
{
    public static PermissionCode HomeRead { get; } = PermissionCode.From("workspace.home.read");
    public static PermissionCode HomeCustomize { get; } = PermissionCode.From("workspace.home.customize");
    public static PermissionCode ProfileSelfRead { get; } = PermissionCode.From("workspace.profile.self.read");
    public static PermissionCode ProfileSelfWrite { get; } = PermissionCode.From("workspace.profile.self.write");
    public static PermissionCode ProfileUserRead { get; } = PermissionCode.From("workspace.profile.user.read");
    public static PermissionCode Search { get; } = PermissionCode.From("workspace.search.read");
    public static PermissionCode FavoritesWrite { get; } = PermissionCode.From("workspace.favorites.write");
    public static PermissionCode RecentsWrite { get; } = PermissionCode.From("workspace.recents.write");
    public static PermissionCode PreferencesWrite { get; } = PermissionCode.From("workspace.preferences.write");
}
