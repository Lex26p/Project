using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Configuration;

public readonly record struct ConfigurationRevisionId
{
    private readonly CanonicalId<ConfigurationRevisionId> value;

    private ConfigurationRevisionId(CanonicalId<ConfigurationRevisionId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static ConfigurationRevisionId New() => new(CanonicalId.New<ConfigurationRevisionId>());

    public static ConfigurationRevisionId From(Guid value) => new(CanonicalId.From<ConfigurationRevisionId>(value));
}

public readonly record struct DistributionJobId
{
    private readonly CanonicalId<DistributionJobId> value;

    private DistributionJobId(CanonicalId<DistributionJobId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static DistributionJobId New() => new(CanonicalId.New<DistributionJobId>());

    public static DistributionJobId From(Guid value) => new(CanonicalId.From<DistributionJobId>(value));
}

public sealed record ConfigurationDependency
{
    public ConfigurationDependency(string key, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (key.Length > 200 || fingerprint.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(key), "Dependency values are limited to 200 characters.");
        }

        Key = key.Trim();
        Fingerprint = fingerprint.Trim();
    }

    public string Key { get; }

    public string Fingerprint { get; }
}

public sealed record SaveConfigurationRequest(
    string ManifestJson,
    IReadOnlyCollection<ConfigurationDependency> Dependencies,
    long? ExpectedVersion);

public sealed record PublishConfigurationRequest(
    ConfigurationRevisionId RevisionId,
    long ExpectedVersion,
    IReadOnlyCollection<ConfigurationDependency> CurrentDependencies);

public sealed record ConfigurationRevisionSnapshot(
    ConfigurationRevisionId RevisionId,
    FacilityScopeId ScopeId,
    RevisionNumber RevisionNumber,
    ConfigurationRevisionId? SourceRevisionId,
    string ManifestJson,
    string ManifestFingerprint,
    IReadOnlyList<ConfigurationDependency> Dependencies,
    string DependencyFingerprint,
    long Version,
    DateTimeOffset SavedAt,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? DistributedAt,
    DateTimeOffset? ActivatedAt);

public sealed record ConfigurationScopeSnapshot(
    FacilityScopeId ScopeId,
    ConfigurationRevisionId? DraftRevisionId,
    ConfigurationRevisionId? PublishedRevisionId,
    ConfigurationRevisionId? DistributedRevisionId,
    ConfigurationRevisionId? ActivatedRevisionId,
    long Version,
    IReadOnlyList<ConfigurationRevisionSnapshot> Revisions);

public sealed record DistributionJobSnapshot(
    DistributionJobId JobId,
    FacilityScopeId ScopeId,
    ConfigurationRevisionId RevisionId,
    string RevisionFingerprint,
    string? ClaimedBy,
    DateTimeOffset? LeaseUntil,
    int Attempts,
    DateTimeOffset? CompletedAt);

public static class ConfigurationPermissions
{
    public static PermissionCode Read(FacilityScopeId scopeId) => Permission(scopeId, "read");

    public static PermissionCode Save(FacilityScopeId scopeId) => Permission(scopeId, "save");

    public static PermissionCode Validate(FacilityScopeId scopeId) => Permission(scopeId, "validate");

    public static PermissionCode Publish(FacilityScopeId scopeId) => Permission(scopeId, "publish");

    public static PermissionCode Distribute(FacilityScopeId scopeId) => Permission(scopeId, "distribute");

    public static PermissionCode Activate(FacilityScopeId scopeId) => Permission(scopeId, "activate");

    private static PermissionCode Permission(FacilityScopeId scopeId, string action) =>
        PermissionCode.From($"configuration.scope.s{scopeId.Value:N}.{action}");
}

internal static class ConfigurationFingerprint
{
    public static (string Json, string Fingerprint) NormalizeManifest(string manifestJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);
        using var document = JsonDocument.Parse(manifestJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A whole-scope configuration manifest must be a JSON object.", nameof(manifestJson));
        }

        var json = Canonicalize(document.RootElement);
        return (json, Compute(json));
    }

    public static (string Json, string Fingerprint, ConfigurationDependency[] Items) NormalizeDependencies(
        IReadOnlyCollection<ConfigurationDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        var items = dependencies
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        if (items.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != items.Length)
        {
            throw new ArgumentException("Dependency keys must be unique.", nameof(dependencies));
        }

        var json = JsonSerializer.Serialize(items);
        return (json, Compute(json), items);
    }

    private static string Compute(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Combine(string manifestFingerprint, string dependencyFingerprint) =>
        Compute($"{manifestFingerprint}:{dependencyFingerprint}");

    private static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(element, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

public static class ConfigurationManifestFingerprint
{
    public static (string Json, string Fingerprint) Normalize(string manifestJson) =>
        ConfigurationFingerprint.NormalizeManifest(manifestJson);
}
