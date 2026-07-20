using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dispatcher.Core;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Dashboards;

internal static class DashboardManifestCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static (string ManifestJson, string ManifestFingerprint, string DependenciesJson, string DependencyFingerprint)
        Encode(DashboardRevision revision)
    {
        Validate(revision);
        var manifest = new ManifestDto(
            revision.DashboardId.Value,
            revision.RevisionId.Value,
            revision.RevisionNumber,
            revision.Name.Trim(),
            revision.Description?.Trim(),
            revision.Windows.Select(window => new WindowDto(
                window.WindowId.Value,
                window.Title.Trim(),
                window.Widgets.Select(widget => new WidgetDto(
                    widget.WidgetId.Value,
                    widget.Kind.Trim(),
                    widget.Title.Trim(),
                    widget.BindingIds.Select(id => id.Value).ToArray())).ToArray(),
                window.Bindings.Select(binding => new BindingDto(
                    binding.BindingId.Value,
                    (int)binding.Source,
                    binding.ScopeId.Value,
                    binding.PointId.Value,
                    binding.RequiredPermission.Value,
                    binding.HistorySourceId?.Value)).ToArray())).ToArray(),
            revision.PublishedAt);
        var dependencies = revision.Dependencies
            .OrderBy(item => item.BindingId.Value)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new DependencyDto(item.BindingId.Value, item.Key.Trim(), item.Fingerprint.Trim()))
            .ToArray();
        var manifestJson = JsonSerializer.Serialize(manifest, Options);
        var dependenciesJson = JsonSerializer.Serialize(dependencies, Options);
        return (manifestJson, Hash(manifestJson), dependenciesJson, Hash(dependenciesJson));
    }

    public static DashboardRevision Decode(string manifestJson, string dependenciesJson)
    {
        var manifest = JsonSerializer.Deserialize<ManifestDto>(manifestJson, Options)
            ?? throw new InvalidOperationException("Dashboard manifest is empty.");
        var dependencies = JsonSerializer.Deserialize<DependencyDto[]>(dependenciesJson, Options) ?? [];
        return new DashboardRevision(
            DashboardId.From(manifest.DashboardId),
            DashboardRevisionId.From(manifest.RevisionId),
            manifest.RevisionNumber,
            manifest.Name,
            manifest.Description,
            manifest.Windows.Select(window => new DashboardWindow(
                DashboardWindowId.From(window.WindowId),
                window.Title,
                window.Widgets.Select(widget => new Widget(
                    WidgetId.From(widget.WidgetId),
                    widget.Kind,
                    widget.Title,
                    widget.BindingIds.Select(DashboardBindingId.From).ToArray())).ToArray(),
                window.Bindings.Select(binding => new DashboardBinding(
                    DashboardBindingId.From(binding.BindingId),
                    (DashboardBindingSource)binding.Source,
                    RuntimeScopeId.From(binding.ScopeId),
                    PointId.From(binding.PointId),
                    PermissionCode.From(binding.RequiredPermission),
                    binding.HistorySourceId is null ? null : SourceId.From(binding.HistorySourceId.Value))).ToArray())).ToArray(),
            dependencies.Select(item => new DashboardDependency(
                DashboardBindingId.From(item.BindingId), item.Key, item.Fingerprint)).ToArray(),
            manifest.PublishedAt);
    }

    private static void Validate(DashboardRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision.Name);
        ArgumentOutOfRangeException.ThrowIfZero(revision.RevisionNumber);
        if (revision.Windows.Count == 0 || revision.Windows.Any(window => string.IsNullOrWhiteSpace(window.Title)))
        {
            throw new ArgumentException("A Dashboard revision must contain named windows.", nameof(revision));
        }

        var windows = revision.Windows.Select(item => item.WindowId).ToArray();
        var widgets = revision.Windows.SelectMany(item => item.Widgets).Select(item => item.WidgetId).ToArray();
        var bindings = revision.Windows.SelectMany(item => item.Bindings).Select(item => item.BindingId).ToArray();
        if (windows.Distinct().Count() != windows.Length ||
            widgets.Distinct().Count() != widgets.Length ||
            bindings.Distinct().Count() != bindings.Length)
        {
            throw new ArgumentException("Dashboard window, widget and binding identifiers must be unique.", nameof(revision));
        }

        var bindingSet = bindings.ToHashSet();
        if (revision.Windows.SelectMany(item => item.Widgets).Any(widget =>
                string.IsNullOrWhiteSpace(widget.Kind) ||
                string.IsNullOrWhiteSpace(widget.Title) ||
                widget.BindingIds.Count == 0 ||
                widget.BindingIds.Any(id => !bindingSet.Contains(id))))
        {
            throw new ArgumentException("Every Widget must reference published revision bindings.", nameof(revision));
        }

        var dependencyBindings = revision.Dependencies.Select(item => item.BindingId).ToHashSet();
        if (revision.Windows.SelectMany(item => item.Bindings).Any(binding =>
                binding.Source == DashboardBindingSource.History && binding.HistorySourceId is null))
        {
            throw new ArgumentException("History bindings require an exact source identity.", nameof(revision));
        }

        if (revision.Dependencies.Count == 0 ||
            !dependencyBindings.SetEquals(bindingSet) ||
            revision.Dependencies.Any(item => !bindingSet.Contains(item.BindingId) ||
                string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Fingerprint)) ||
            revision.Dependencies.Select(item => (item.BindingId, item.Key)).Distinct().Count() != revision.Dependencies.Count)
        {
            throw new ArgumentException("Dashboard dependencies must be exact, unique and binding-scoped.", nameof(revision));
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record ManifestDto(
        Guid DashboardId,
        Guid RevisionId,
        ulong RevisionNumber,
        string Name,
        string? Description,
        WindowDto[] Windows,
        DateTimeOffset PublishedAt);
    private sealed record WindowDto(Guid WindowId, string Title, WidgetDto[] Widgets, BindingDto[] Bindings);
    private sealed record WidgetDto(Guid WidgetId, string Kind, string Title, Guid[] BindingIds);
    private sealed record BindingDto(
        Guid BindingId, int Source, Guid ScopeId, Guid PointId, string RequiredPermission, Guid? HistorySourceId);
    private sealed record DependencyDto(Guid BindingId, string Key, string Fingerprint);
}
