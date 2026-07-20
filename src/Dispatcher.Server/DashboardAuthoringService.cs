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

    public Task<Result<DashboardAuthoringRevisionSnapshot>> ValidateDashboardAsync(
        SessionSnapshot? session,
        DashboardId dashboardId,
        Guid revisionId,
        long expectedVersion,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            session,
            DashboardEditorPermissions.Validate(dashboardId),
            authorization => store.ValidateAuthoringAsync(
                authorization, dashboardId.Value, DashboardAuthoringKind.Dashboard,
                revisionId, expectedVersion, cancellationToken));

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
}
