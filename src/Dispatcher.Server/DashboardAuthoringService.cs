using Dispatcher.Dashboards;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Server;

public sealed class DashboardAuthoringService
{
    private readonly DashboardStore store;
    private readonly SvgIntakeLimits svgLimits;
    private readonly IWallClock clock;
    private readonly DashboardSubscriptionGenerationStore generations;

    public DashboardAuthoringService(
        DashboardStore store,
        SvgIntakeLimits svgLimits,
        IWallClock clock,
        DashboardSubscriptionGenerationStore generations)
    {
        this.store = store;
        this.svgLimits = svgLimits;
        this.clock = clock;
        this.generations = generations;
    }

    public Task<Result<DashboardAuthoringRevisionSnapshot>> SaveDashboardAsync(
        SessionSnapshot? session,
        DashboardId dashboardId,
        SaveDashboardDraftRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            session,
            DashboardEditorPermissions.Save(dashboardId),
            authorization => store.SaveDashboardDraftAsync(
                authorization, dashboardId, request, cancellationToken));

    public Task<Result<DashboardDraftSnapshot?>> ReadDashboardAsync(
        SessionSnapshot? session,
        DashboardId dashboardId,
        CancellationToken cancellationToken) =>
        ReadAsync(
            session,
            DashboardEditorPermissions.Read(dashboardId),
            () => store.ReadDashboardDraftAsync(dashboardId, cancellationToken));

    public Task<Result<DashboardAuthoringRevisionSnapshot>> SaveMimicAsync(
        SessionSnapshot? session,
        MimicId mimicId,
        SaveMimicDraftRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            session,
            MimicEditorPermissions.Save(mimicId),
            authorization => store.SaveMimicDraftAsync(
                authorization, mimicId, request, svgLimits, cancellationToken));

    public Task<Result<MimicDraftSnapshot?>> ReadMimicAsync(
        SessionSnapshot? session,
        MimicId mimicId,
        CancellationToken cancellationToken) =>
        ReadAsync(
            session,
            MimicEditorPermissions.Read(mimicId),
            () => store.ReadMimicDraftAsync(mimicId, cancellationToken));

    public async Task<Result<
        DashboardPublicationImpact>>
        ReadDashboardImpactAsync(
            SessionSnapshot? session,
            DashboardId dashboardId,
            Guid revisionId,
            CancellationToken cancellationToken)
    {
        var authorization =
            SessionAuthorization.AuthorizeAccess(
                session,
                DashboardEditorPermissions.Read(
                    dashboardId),
                clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<
                DashboardPublicationImpact>(
                authorization.Error!);
        }

        var draft =
            await store.ReadDashboardDraftAsync(
                    dashboardId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (draft is null ||
            draft.Revision.RevisionId != revisionId)
        {
            return Result.Failure<
                DashboardPublicationImpact>(
                new OperationError(
                    ErrorCode.From(
                        "dashboard.draft_not_found"),
                    "Editor draft was not found."));
        }

        var published =
            await store.ReadPublishedAsync(
                    dashboardId,
                    cancellationToken)
                .ConfigureAwait(false);
        return Result.Success(
            new DashboardPublicationImpact(
                revisionId,
                published?.Revision.RevisionId
                    .Value,
                draft.Content.Windows
                    .Select(item =>
                        item.WindowId.Value)
                    .ToArray(),
                draft.Content.Windows
                    .Sum(item =>
                        item.Bindings.Count),
                draft.Content.Windows.Count(
                    item => item.Mimic is not null),
                true));
    }

    public async Task<Result<
        DashboardPublicationImpact>>
        ReadMimicImpactAsync(
            SessionSnapshot? session,
            MimicId mimicId,
            Guid revisionId,
            CancellationToken cancellationToken)
    {
        var authorization =
            SessionAuthorization.AuthorizeAccess(
                session,
                MimicEditorPermissions.Read(
                    mimicId),
                clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<
                DashboardPublicationImpact>(
                authorization.Error!);
        }

        var draft =
            await store.ReadMimicDraftAsync(
                    mimicId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (draft is null ||
            draft.Revision.RevisionId != revisionId)
        {
            return Result.Failure<
                DashboardPublicationImpact>(
                new OperationError(
                    ErrorCode.From(
                        "dashboard.draft_not_found"),
                    "Mimic draft was not found."));
        }

        var state =
            await store.ReadAuthoringStateAsync(
                    mimicId.Value,
                    cancellationToken)
                .ConfigureAwait(false);
        return Result.Success(
            new DashboardPublicationImpact(
                revisionId,
                state?.PublishedRevisionId,
                [],
                draft.Content.Bindings.Count,
                1,
                true));
    }

    public Result<string> PreviewMimic(
        SessionSnapshot? session,
        MimicId mimicId,
        MimicDraftContent content)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(
            session, MimicEditorPermissions.Read(mimicId), clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<string>(authorization.Error!);
        }

        try
        {
            return Result.Success(MimicSvgSanitizer.Sanitize(content, svgLimits));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<string>(new OperationError(
                ErrorCode.From("dashboard.content_invalid"), exception.Message));
        }
    }

    public async Task<Result<DashboardAuthoringRevisionSnapshot>> ValidateDashboardAsync(
        SessionSnapshot? session,
        DashboardId dashboardId,
        Guid revisionId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var authorization =
            SessionAuthorization.AuthorizeMutation(
                session,
                DashboardEditorPermissions.Validate(
                    dashboardId),
                clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<
                DashboardAuthoringRevisionSnapshot>(
                authorization.Error!);
        }

        var draft =
            await store.ReadDashboardDraftAsync(
                    dashboardId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (draft is null ||
            draft.Revision.RevisionId != revisionId ||
            draft.Revision.Version != expectedVersion)
        {
            return Result.Failure<
                DashboardAuthoringRevisionSnapshot>(
                new OperationError(
                    ErrorCode.From(
                        "dashboard.version_conflict"),
                    "Editor draft changed concurrently."));
        }

        foreach (var reference in
                 draft.Content.Windows
                     .Select(window => window.Mimic)
                     .OfType<
                         DashboardMimicReference>())
        {
            if (await store.ReadPublishedMimicAsync(
                        reference.MimicId,
                        reference.RevisionId,
                        cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return Result.Failure<
                    DashboardAuthoringRevisionSnapshot>(
                    new OperationError(
                        ErrorCode.From(
                            "dashboard.validation_failed"),
                        "A window references a missing or unpublished Mimic revision."));
            }
        }

        return await store.ValidateAuthoringAsync(
                authorization.Value,
                dashboardId.Value,
                DashboardAuthoringKind.Dashboard,
                revisionId,
                expectedVersion,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<Result<DashboardAuthoringRevisionSnapshot>> ValidateMimicAsync(
        SessionSnapshot? session,
        MimicId mimicId,
        Guid revisionId,
        long expectedVersion,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            session,
            MimicEditorPermissions.Validate(mimicId),
            authorization => store.ValidateAuthoringAsync(
                authorization, mimicId.Value, DashboardAuthoringKind.Mimic,
                revisionId, expectedVersion, cancellationToken));

    public async Task<Result<DashboardAuthoringRevisionSnapshot>> PublishDashboardAsync(
        SessionSnapshot? session,
        DashboardId dashboardId,
        PublishAuthoringRequest request,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(
            session,
            DashboardEditorPermissions.Publish(dashboardId),
            authorization => store.PublishAuthoringAsync(
                authorization, dashboardId.Value, DashboardAuthoringKind.Dashboard,
                request, cancellationToken)).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            generations.ClosePreviousGenerations(
                dashboardId,
                DashboardRevisionId.From(result.Value.RevisionId));
        }

        return result;
    }

    public Task<Result<DashboardAuthoringRevisionSnapshot>> PublishMimicAsync(
        SessionSnapshot? session,
        MimicId mimicId,
        PublishAuthoringRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            session,
            MimicEditorPermissions.Publish(mimicId),
            authorization => store.PublishAuthoringAsync(
                authorization, mimicId.Value, DashboardAuthoringKind.Mimic,
                request, cancellationToken));

    public Task<Result<DashboardAuthoringRevisionSnapshot>> RollbackDashboardAsync(
        SessionSnapshot? session,
        DashboardId dashboardId,
        Guid sourceRevisionId,
        long expectedResourceVersion,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            session,
            DashboardEditorPermissions.Rollback(dashboardId),
            authorization => store.RollbackAuthoringAsync(
                authorization, dashboardId.Value, DashboardAuthoringKind.Dashboard,
                sourceRevisionId, expectedResourceVersion, cancellationToken));

    public Task<Result<DashboardAuthoringRevisionSnapshot>> RollbackMimicAsync(
        SessionSnapshot? session,
        MimicId mimicId,
        Guid sourceRevisionId,
        long expectedResourceVersion,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            session,
            MimicEditorPermissions.Rollback(mimicId),
            authorization => store.RollbackAuthoringAsync(
                authorization, mimicId.Value, DashboardAuthoringKind.Mimic,
                sourceRevisionId, expectedResourceVersion, cancellationToken));

    private Task<Result<DashboardAuthoringRevisionSnapshot>> ExecuteAsync(
        SessionSnapshot? session,
        PermissionCode permission,
        Func<AuthorizedMutation, Task<Result<DashboardAuthoringRevisionSnapshot>>> action)
    {
        var authorization = SessionAuthorization.AuthorizeMutation(session, permission, clock);
        return authorization.IsSuccess
            ? action(authorization.Value)
            : Task.FromResult(Result.Failure<DashboardAuthoringRevisionSnapshot>(authorization.Error!));
    }

    private async Task<Result<T?>> ReadAsync<T>(
        SessionSnapshot? session,
        PermissionCode permission,
        Func<Task<T?>> read)
        where T : class
    {
        var authorization = SessionAuthorization.AuthorizeAccess(session, permission, clock);
        return authorization.IsSuccess
            ? Result.Success(await read().ConfigureAwait(false))
            : Result.Failure<T?>(authorization.Error!);
    }
}

public sealed record DashboardPublicationImpact(
    Guid RevisionId,
    Guid? ReplacesRevisionId,
    IReadOnlyList<Guid> WindowIds,
    int BindingCount,
    int MimicCount,
    bool RequiresRuntimeResnapshot);
