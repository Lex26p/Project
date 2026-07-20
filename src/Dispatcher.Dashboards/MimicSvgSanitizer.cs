using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Dispatcher.Dashboards;

public static class MimicSvgSanitizer
{
    private static readonly HashSet<string> AllowedElements = new(StringComparer.Ordinal)
    {
        "svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text",
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.Ordinal)
    {
        "id", "viewBox", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry",
        "width", "height", "d", "points", "fill", "stroke", "stroke-width", "opacity", "transform",
        "font-size", "text-anchor", "role", "aria-label", "data-binding-id",
    };

    public static (string Json, string ContentFingerprint, string DependenciesJson, string DependencyFingerprint)
        Normalize(MimicDraftContent content, SvgIntakeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentException.ThrowIfNullOrWhiteSpace(content.Name);
        var bytes = Encoding.UTF8.GetByteCount(content.Svg);
        if (bytes == 0 || bytes > limits.MaxUtf8Bytes)
        {
            throw new ArgumentException("SVG intake size is outside the configured bound.", nameof(content));
        }

        XDocument document;
        try
        {
            using var stringReader = new StringReader(content.Svg);
            using var reader = XmlReader.Create(stringReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = limits.MaxUtf8Bytes,
            });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new ArgumentException("SVG is not safe well-formed XML.", nameof(content), exception);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "svg" ||
            root.Name.NamespaceName != "http://www.w3.org/2000/svg")
        {
            throw new ArgumentException("SVG root and namespace are required.", nameof(content));
        }

        var elements = root.DescendantsAndSelf().ToArray();
        if (elements.Length > limits.MaxElements)
        {
            throw new ArgumentException("SVG element capacity was exceeded.", nameof(content));
        }

        var referencedBindings = new HashSet<DashboardBindingId>();
        foreach (var element in elements)
        {
            if (element.Name.NamespaceName != root.Name.NamespaceName || !AllowedElements.Contains(element.Name.LocalName))
            {
                throw new ArgumentException($"SVG element '{element.Name.LocalName}' is not allowed.", nameof(content));
            }

            var attributes = element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration).ToArray();
            if (attributes.Length > limits.MaxAttributesPerElement)
            {
                throw new ArgumentException("SVG attribute capacity was exceeded.", nameof(content));
            }

            foreach (var attribute in attributes)
            {
                if (attribute.Name.NamespaceName.Length > 0 ||
                    !AllowedAttributes.Contains(attribute.Name.LocalName) ||
                    attribute.Value.Length > limits.MaxAttributeLength ||
                    attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                    attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase) ||
                    attribute.Value.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
                    attribute.Value.Contains("data:", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"SVG attribute '{attribute.Name.LocalName}' is not allowed.", nameof(content));
                }

                if (attribute.Name.LocalName == "data-binding-id")
                {
                    if (!Guid.TryParse(attribute.Value, out var bindingId))
                    {
                        throw new ArgumentException("SVG binding identity is invalid.", nameof(content));
                    }

                    referencedBindings.Add(DashboardBindingId.From(bindingId));
                }
            }
        }

        var bindings = content.Bindings.Select(binding => binding.BindingId).ToHashSet();
        if (!referencedBindings.SetEquals(bindings) ||
            content.Bindings.Select(binding => binding.BindingId).Distinct().Count() != content.Bindings.Count)
        {
            throw new ArgumentException("SVG bindings must exactly match declared Mimic bindings.", nameof(content));
        }

        var dependencyBindings = content.Dependencies.Select(item => item.BindingId).ToHashSet();
        if (!dependencyBindings.SetEquals(bindings) ||
            content.Dependencies.Select(item => (item.BindingId, item.Key)).Distinct().Count() != content.Dependencies.Count ||
            content.Dependencies.Any(item => string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Fingerprint)))
        {
            throw new ArgumentException("Mimic dependencies must exactly cover declared bindings.", nameof(content));
        }

        var svg = document.ToString(SaveOptions.DisableFormatting);
        var dto = new MimicDto(
            content.Name.Trim(),
            svg,
            content.Bindings.Select(binding => new BindingDto(
                binding.BindingId.Value,
                (int)binding.Source,
                binding.ScopeId.Value,
                binding.PointId.Value,
                binding.RequiredPermission.Value,
                binding.HistorySourceId?.Value)).ToArray());
        var dependencies = content.Dependencies
            .OrderBy(item => item.BindingId.Value)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new DependencyDto(item.BindingId.Value, item.Key.Trim(), item.Fingerprint.Trim()))
            .ToArray();
        var json = JsonSerializer.Serialize(dto);
        var dependenciesJson = JsonSerializer.Serialize(dependencies);
        return (json, Hash(json), dependenciesJson, Hash(dependenciesJson));
    }

    public static string Sanitize(MimicDraftContent content, SvgIntakeLimits limits)
    {
        var normalized = Normalize(content, limits);
        return JsonSerializer.Deserialize<MimicDto>(normalized.Json)?.Svg
            ?? throw new ArgumentException("Sanitized SVG is empty.", nameof(content));
    }

    internal static MimicDraftContent Decode(string contentJson, string dependenciesJson)
    {
        var content = JsonSerializer.Deserialize<MimicDto>(contentJson)
            ?? throw new InvalidOperationException("Mimic content is empty.");
        var dependencies = JsonSerializer.Deserialize<DependencyDto[]>(dependenciesJson) ?? [];
        return new MimicDraftContent(
            content.Name,
            content.Svg,
            content.Bindings.Select(binding => new DashboardBinding(
                DashboardBindingId.From(binding.BindingId),
                (DashboardBindingSource)binding.Source,
                Dispatcher.Core.RuntimeScopeId.From(binding.ScopeId),
                Dispatcher.Semantics.PointId.From(binding.PointId),
                Dispatcher.Platform.PermissionCode.From(binding.RequiredPermission),
                binding.HistorySourceId is null
                    ? null
                    : Dispatcher.Core.SourceId.From(binding.HistorySourceId.Value))).ToArray(),
            dependencies.Select(item => new DashboardDependency(
                DashboardBindingId.From(item.BindingId), item.Key, item.Fingerprint)).ToArray());
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record MimicDto(string Name, string Svg, BindingDto[] Bindings);
    private sealed record BindingDto(
        Guid BindingId, int Source, Guid ScopeId, Guid PointId, string RequiredPermission, Guid? HistorySourceId);
    private sealed record DependencyDto(Guid BindingId, string Key, string Fingerprint);
}
