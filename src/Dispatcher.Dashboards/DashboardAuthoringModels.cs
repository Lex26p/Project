using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Dashboards;

public readonly record struct MimicId
{
    private readonly CanonicalId<MimicId> value;
    private MimicId(CanonicalId<MimicId> value) => this.value = value;
    public Guid Value => value.Value;
    public static MimicId New() => new(CanonicalId.New<MimicId>());
    public static MimicId From(Guid value) => new(CanonicalId.From<MimicId>(value));
}

public readonly record struct MimicRevisionId
{
    private readonly CanonicalId<MimicRevisionId> value;
    private MimicRevisionId(CanonicalId<MimicRevisionId> value) => this.value = value;
    public Guid Value => value.Value;
    public static MimicRevisionId New() => new(CanonicalId.New<MimicRevisionId>());
    public static MimicRevisionId From(Guid value) => new(CanonicalId.From<MimicRevisionId>(value));
}

public enum DashboardAuthoringKind
{
    Dashboard = 1,
    Mimic = 2,
}

public sealed record DashboardDraftContent(
    string Name,
    string? Description,
    IReadOnlyList<DashboardWindow> Windows,
    IReadOnlyList<DashboardDependency> Dependencies);

public sealed record MimicDraftContent(
    string Name,
    string Svg,
    IReadOnlyList<DashboardBinding> Bindings,
    IReadOnlyList<DashboardDependency> Dependencies);

public sealed record SaveDashboardDraftRequest(DashboardDraftContent Content, long? ExpectedVersion);
public sealed record SaveMimicDraftRequest(MimicDraftContent Content, long? ExpectedVersion);
public sealed record PublishAuthoringRequest(Guid RevisionId, long ExpectedVersion);

public sealed record DashboardAuthoringRevisionSnapshot(
    Guid ResourceId,
    DashboardAuthoringKind Kind,
    Guid RevisionId,
    ulong RevisionNumber,
    Guid? SourceRevisionId,
    long Version,
    string ContentFingerprint,
    string DependencyFingerprint,
    DateTimeOffset SavedAt,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset? PublishedAt);

public sealed record DashboardAuthoringState(
    Guid ResourceId,
    DashboardAuthoringKind Kind,
    Guid? DraftRevisionId,
    Guid? PublishedRevisionId,
    long Version);

public sealed record DashboardDraftSnapshot(
    DashboardAuthoringRevisionSnapshot Revision,
    DashboardDraftContent Content);

public sealed record MimicDraftSnapshot(
    DashboardAuthoringRevisionSnapshot Revision,
    MimicDraftContent Content);

public sealed record SvgIntakeLimits
{
    public SvgIntakeLimits(int maxUtf8Bytes, int maxElements, int maxAttributesPerElement, int maxAttributeLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxUtf8Bytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxElements);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttributesPerElement);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttributeLength);
        MaxUtf8Bytes = maxUtf8Bytes;
        MaxElements = maxElements;
        MaxAttributesPerElement = maxAttributesPerElement;
        MaxAttributeLength = maxAttributeLength;
    }

    public int MaxUtf8Bytes { get; }
    public int MaxElements { get; }
    public int MaxAttributesPerElement { get; }
    public int MaxAttributeLength { get; }
}

public static class DashboardEditorPermissions
{
    public static PermissionCode Read(DashboardId id) => Permission(id, "read");
    public static PermissionCode Save(DashboardId id) => Permission(id, "save");
    public static PermissionCode Validate(DashboardId id) => Permission(id, "validate");
    public static PermissionCode Publish(DashboardId id) => Permission(id, "publish");
    public static PermissionCode Rollback(DashboardId id) => Permission(id, "rollback");
    private static PermissionCode Permission(DashboardId id, string action) =>
        PermissionCode.From($"dashboards.d{id.Value:N}.editor.{action}");
}

public static class MimicEditorPermissions
{
    public static PermissionCode Read(MimicId id) => Permission(id, "read");
    public static PermissionCode Save(MimicId id) => Permission(id, "save");
    public static PermissionCode Validate(MimicId id) => Permission(id, "validate");
    public static PermissionCode Publish(MimicId id) => Permission(id, "publish");
    public static PermissionCode Rollback(MimicId id) => Permission(id, "rollback");
    private static PermissionCode Permission(MimicId id, string action) =>
        PermissionCode.From($"mimics.m{id.Value:N}.editor.{action}");
}
