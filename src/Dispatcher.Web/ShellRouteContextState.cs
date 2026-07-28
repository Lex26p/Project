using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace Dispatcher.Web;

public sealed record ShellRouteContext(
    string Section,
    string? EntityKind,
    Guid? EntityId,
    string Path)
{
    public bool HasEntity =>
        EntityKind is not null &&
        EntityId is not null;

    public string EntityLabel =>
        HasEntity
            ? $"{EntityKind} {Short(EntityId!.Value)}"
            : "Section overview";

    private static string Short(Guid value) =>
        value.ToString("N")[..8].ToUpperInvariant();
}

public sealed class ShellRouteContextState :
    IDisposable
{
    private readonly NavigationManager navigation;

    public ShellRouteContextState(
        NavigationManager navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        this.navigation = navigation;
        Current = Parse(
            navigation.ToBaseRelativePath(
                navigation.Uri));
        navigation.LocationChanged +=
            HandleLocationChanged;
    }

    public ShellRouteContext Current { get; private set; }

    public event Action? Changed;

    public static ShellRouteContext Parse(
        string? relativeUri)
    {
        var path = NormalizePath(relativeUri);
        var segments = path
            .Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return new ShellRouteContext(
                "Home",
                EntityKind: null,
                EntityId: null,
                path);
        }

        var section =
            SectionLabel(segments);
        var entity =
            EntityContext(segments);
        return new ShellRouteContext(
            section,
            entity.Kind,
            entity.Id,
            path);
    }

    public void Dispose()
    {
        navigation.LocationChanged -=
            HandleLocationChanged;
        GC.SuppressFinalize(this);
    }

    private void HandleLocationChanged(
        object? sender,
        LocationChangedEventArgs args)
    {
        var next = Parse(
            navigation.ToBaseRelativePath(
                args.Location));
        if (next == Current)
        {
            return;
        }

        Current = next;
        Changed?.Invoke();
    }

    private static string NormalizePath(
        string? relativeUri)
    {
        if (string.IsNullOrWhiteSpace(
                relativeUri))
        {
            return "/";
        }

        var path = relativeUri
            .Split('#', 2)[0]
            .Split('?', 2)[0]
            .Trim();
        return path.Length == 0
            ? "/"
            : $"/{path.TrimStart('/')}";
    }

    private static string SectionLabel(
        string[] segments) =>
        segments[0].ToLowerInvariant() switch
        {
            "home" => "Home",
            "current" => "Current",
            "history" => "History",
            "locations" => "Locations",
            "equipment" => "Equipment",
            "dashboards" => "Dashboards",
            "control" => "Control",
            "operations" => "Operations",
            "administration" =>
                segments.Length > 1
                    ? $"Administration · {Titleize(segments[1])}"
                    : "Administration",
            "search" => "Search",
            "profile" => "Profile",
            _ => Titleize(segments[0]),
        };

    private static (
        string? Kind,
        Guid? Id) EntityContext(
            string[] segments)
    {
        if (segments.Length < 2 ||
            !Guid.TryParse(
                segments[1],
                out var identifier))
        {
            return (null, null);
        }

        return segments[0]
            .ToLowerInvariant() switch
        {
            "locations" =>
                ("Location", identifier),
            "equipment" =>
                ("Equipment", identifier),
            "dashboards" =>
                ("Dashboard", identifier),
            _ => (null, null),
        };
    }

    private static string Titleize(
        string value)
    {
        var words = value
            .Replace(
                "-",
                " ",
                StringComparison.Ordinal)
            .Replace(
                "_",
                " ",
                StringComparison.Ordinal)
            .Split(
                ' ',
                StringSplitOptions
                    .RemoveEmptyEntries);
        return string.Join(
            " ",
            words.Select(
                word =>
                    char.ToUpperInvariant(
                        word[0]) +
                    word[1..]));
    }
}
