using Dispatcher.Core;
using Dispatcher.Dashboards;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Server;

public static class DashboardAuthoringEndpoints
{
    public static IEndpointRouteBuilder MapDashboardAuthoringServer(this IEndpointRouteBuilder endpoints)
    {
        var dashboards = endpoints.MapGroup("/api/dashboard-editor");
        dashboards.MapGet("/{dashboardId:guid}", ReadDashboardAsync);
        dashboards.MapPut("/{dashboardId:guid}/draft", SaveDashboardAsync);
        dashboards.MapPost("/{dashboardId:guid}/validate", ValidateDashboardAsync);
        dashboards.MapPost("/{dashboardId:guid}/publish", PublishDashboardAsync);

        var mimics = endpoints.MapGroup("/api/mimic-editor");
        mimics.MapGet("/{mimicId:guid}", ReadMimicAsync);
        mimics.MapPost("/{mimicId:guid}/preview", PreviewMimic);
        mimics.MapPut("/{mimicId:guid}/draft", SaveMimicAsync);
        mimics.MapPost("/{mimicId:guid}/validate", ValidateMimicAsync);
        mimics.MapPost("/{mimicId:guid}/publish", PublishMimicAsync);
        return endpoints;
    }

    private static async Task<IResult> ReadDashboardAsync(
        Guid dashboardId,
        HttpContext context,
        RequestSessionResolver sessions,
        DashboardAuthoringService authoring,
        CancellationToken cancellationToken)
    {
        var result = await authoring.ReadDashboardAsync(
            sessions.Resolve(context), DashboardId.From(dashboardId), cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? Problem(result.Error!)
            : result.Value is null ? Results.NoContent()
            : Results.Ok(new DashboardEditorDraftPayload(
                ToPayload(result.Value.Revision), ToPayload(result.Value.Content)));
    }

    private static async Task<IResult> SaveDashboardAsync(
        Guid dashboardId,
        SaveDashboardEditorRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        DashboardAuthoringService authoring,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await authoring.SaveDashboardAsync(
                sessions.Resolve(context),
                DashboardId.From(dashboardId),
                new SaveDashboardDraftRequest(ToDomain(request.Document), request.ExpectedVersion),
                cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(ToPayload(result.Value)) : Problem(result.Error!);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "dashboard.content_invalid", detail = exception.Message });
        }
    }

    private static Task<IResult> ValidateDashboardAsync(
        Guid dashboardId,
        EditorRevisionRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        DashboardAuthoringService authoring,
        CancellationToken cancellationToken) =>
        RevisionResultAsync(authoring.ValidateDashboardAsync(
            sessions.Resolve(context), DashboardId.From(dashboardId),
            request.RevisionId, request.ExpectedVersion, cancellationToken));

    private static Task<IResult> PublishDashboardAsync(
        Guid dashboardId,
        EditorRevisionRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        DashboardAuthoringService authoring,
        CancellationToken cancellationToken) =>
        RevisionResultAsync(authoring.PublishDashboardAsync(
            sessions.Resolve(context), DashboardId.From(dashboardId),
            new PublishAuthoringRequest(request.RevisionId, request.ExpectedVersion), cancellationToken));

    private static async Task<IResult> ReadMimicAsync(
        Guid mimicId,
        HttpContext context,
        RequestSessionResolver sessions,
        DashboardAuthoringService authoring,
        CancellationToken cancellationToken)
    {
        var result = await authoring.ReadMimicAsync(
            sessions.Resolve(context), MimicId.From(mimicId), cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? Problem(result.Error!)
            : result.Value is null ? Results.NoContent()
            : Results.Ok(new MimicEditorDraftPayload(
                ToPayload(result.Value.Revision), ToPayload(result.Value.Content)));
    }

    private static IResult PreviewMimic(
        Guid mimicId,
        MimicEditorDocumentPayload document,
        HttpContext context,
        RequestSessionResolver sessions,
        DashboardAuthoringService authoring)
    {
        try
        {
            var result = authoring.PreviewMimic(
                sessions.Resolve(context), MimicId.From(mimicId), ToDomain(document));
            return result.IsSuccess
                ? Results.Ok(new MimicPreviewPayload(result.Value))
                : Problem(result.Error!);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "dashboard.content_invalid", detail = exception.Message });
        }
    }

    private static async Task<IResult> SaveMimicAsync(
        Guid mimicId,
        SaveMimicEditorRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        DashboardAuthoringService authoring,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await authoring.SaveMimicAsync(
                sessions.Resolve(context),
                MimicId.From(mimicId),
                new SaveMimicDraftRequest(ToDomain(request.Document), request.ExpectedVersion),
                cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(ToPayload(result.Value)) : Problem(result.Error!);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "dashboard.content_invalid", detail = exception.Message });
        }
    }

    private static Task<IResult> ValidateMimicAsync(
        Guid mimicId,
        EditorRevisionRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        DashboardAuthoringService authoring,
        CancellationToken cancellationToken) =>
        RevisionResultAsync(authoring.ValidateMimicAsync(
            sessions.Resolve(context), MimicId.From(mimicId),
            request.RevisionId, request.ExpectedVersion, cancellationToken));

    private static Task<IResult> PublishMimicAsync(
        Guid mimicId,
        EditorRevisionRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        DashboardAuthoringService authoring,
        CancellationToken cancellationToken) =>
        RevisionResultAsync(authoring.PublishMimicAsync(
            sessions.Resolve(context), MimicId.From(mimicId),
            new PublishAuthoringRequest(request.RevisionId, request.ExpectedVersion), cancellationToken));

    private static async Task<IResult> RevisionResultAsync(
        Task<Result<DashboardAuthoringRevisionSnapshot>> task)
    {
        var result = await task.ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(ToPayload(result.Value)) : Problem(result.Error!);
    }

    private static DashboardDraftContent ToDomain(DashboardEditorDocumentPayload payload) => new(
        payload.Name,
        payload.Description,
        payload.Windows.Select(window => new DashboardWindow(
            DashboardWindowId.From(window.WindowId),
            window.Title,
            window.Widgets.Select(widget => new Widget(
                WidgetId.From(widget.WidgetId), widget.Kind, widget.Title,
                widget.BindingIds.Select(DashboardBindingId.From).ToArray())).ToArray(),
            window.Bindings.Select(ToDomain).ToArray())).ToArray(),
        payload.Dependencies.Select(item => new DashboardDependency(
            DashboardBindingId.From(item.BindingId), item.Key, item.Fingerprint)).ToArray());

    private static MimicDraftContent ToDomain(MimicEditorDocumentPayload payload) => new(
        payload.Name,
        payload.Svg,
        payload.Bindings.Select(ToDomain).ToArray(),
        payload.Dependencies.Select(item => new DashboardDependency(
            DashboardBindingId.From(item.BindingId), item.Key, item.Fingerprint)).ToArray());

    private static DashboardBinding ToDomain(EditorBindingPayload binding) => new(
        DashboardBindingId.From(binding.BindingId),
        Enum.Parse<DashboardBindingSource>(binding.Source, ignoreCase: true),
        RuntimeScopeId.From(binding.ScopeId),
        PointId.From(binding.PointId),
        PermissionCode.From(binding.RequiredPermission),
        binding.HistorySourceId is null ? null : SourceId.From(binding.HistorySourceId.Value));

    private static DashboardEditorDocumentPayload ToPayload(DashboardDraftContent content) => new(
        content.Name,
        content.Description,
        content.Windows.Select(window => new EditorWindowPayload(
            window.WindowId.Value,
            window.Title,
            window.Widgets.Select(widget => new EditorWidgetPayload(
                widget.WidgetId.Value, widget.Kind, widget.Title,
                widget.BindingIds.Select(id => id.Value).ToArray())).ToArray(),
            window.Bindings.Select(ToPayload).ToArray())).ToArray(),
        content.Dependencies.Select(item => new EditorDependencyPayload(
            item.BindingId.Value, item.Key, item.Fingerprint)).ToArray());

    private static MimicEditorDocumentPayload ToPayload(MimicDraftContent content) => new(
        content.Name,
        content.Svg,
        content.Bindings.Select(ToPayload).ToArray(),
        content.Dependencies.Select(item => new EditorDependencyPayload(
            item.BindingId.Value, item.Key, item.Fingerprint)).ToArray());

    private static EditorBindingPayload ToPayload(DashboardBinding binding) => new(
        binding.BindingId.Value,
        binding.Source.ToString(),
        binding.ScopeId.Value,
        binding.PointId.Value,
        binding.RequiredPermission.Value,
        binding.HistorySourceId?.Value);

    private static EditorRevisionPayload ToPayload(DashboardAuthoringRevisionSnapshot revision) => new(
        revision.RevisionId,
        revision.RevisionNumber,
        revision.Version,
        revision.ValidatedAt,
        revision.PublishedAt);

    private static IResult Problem(OperationError error) => Results.Problem(
        statusCode: error.Code.Value switch
        {
            "session.anonymous" or "session.revoked" or "session.expired" => StatusCodes.Status401Unauthorized,
            "permission.denied" => StatusCodes.Status403Forbidden,
            "dashboard.version_conflict" or "dashboard.validation_stale" or "dashboard.draft_exists" =>
                StatusCodes.Status409Conflict,
            "dashboard.draft_not_found" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        },
        title: error.Code.Value,
        detail: error.Message);
}

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
