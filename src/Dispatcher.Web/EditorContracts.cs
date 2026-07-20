namespace Dispatcher.Web;

public sealed record EditorRevisionRequest(Guid RevisionId, long ExpectedVersion);
public sealed record SaveDashboardEditorRequest(long? ExpectedVersion, DashboardEditorDocumentPayload Document);
public sealed record SaveMimicEditorRequest(long? ExpectedVersion, MimicEditorDocumentPayload Document);
public sealed record DashboardEditorDraftPayload(EditorRevisionPayload Revision, DashboardEditorDocumentPayload Document);
public sealed record MimicEditorDraftPayload(EditorRevisionPayload Revision, MimicEditorDocumentPayload Document);
public sealed record EditorRevisionPayload(
    Guid RevisionId, ulong RevisionNumber, long Version, DateTimeOffset? ValidatedAt, DateTimeOffset? PublishedAt);
public sealed record DashboardEditorDocumentPayload(
    string Name, string? Description, IReadOnlyList<EditorWindowPayload> Windows,
    IReadOnlyList<EditorDependencyPayload> Dependencies);
public sealed record MimicEditorDocumentPayload(
    string Name, string Svg, IReadOnlyList<EditorBindingPayload> Bindings,
    IReadOnlyList<EditorDependencyPayload> Dependencies);
public sealed record EditorWindowPayload(
    Guid WindowId, string Title, IReadOnlyList<EditorWidgetPayload> Widgets,
    IReadOnlyList<EditorBindingPayload> Bindings);
public sealed record EditorWidgetPayload(
    Guid WidgetId, string Kind, string Title, IReadOnlyList<Guid> BindingIds);
public sealed record EditorBindingPayload(
    Guid BindingId, string Source, Guid ScopeId, Guid PointId,
    string RequiredPermission, Guid? HistorySourceId);
public sealed record EditorDependencyPayload(Guid BindingId, string Key, string Fingerprint);
public sealed record MimicPreviewPayload(string SanitizedSvg);

public sealed class EditorAccessDeniedException : Exception;
public sealed class EditorConflictException : Exception;
