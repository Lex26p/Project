using Dispatcher.Events;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Maintenance;

public sealed class MaintenanceWorkService
{
    private readonly MaintenanceWorkStore workStore;
    private readonly MaintenanceStore assetStore;
    private readonly IWallClock clock;
    private readonly MaintenanceQueryLimits limits;

    public MaintenanceWorkService(
        MaintenanceWorkStore workStore,
        MaintenanceStore assetStore,
        IWallClock clock,
        MaintenanceQueryLimits? limits = null)
    {
        this.workStore = workStore ?? throw new ArgumentNullException(nameof(workStore));
        this.assetStore = assetStore ?? throw new ArgumentNullException(nameof(assetStore));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.limits = limits ?? MaintenanceQueryLimits.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.limits.MaximumPageSize);
    }

    public async Task<Result<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>> CreateRequestAsync(
        SessionSnapshot? session,
        CreateMaintenanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = await ResolveAssetMutationAsync(session, request.AssetId, cancellationToken).ConfigureAwait(false);
        return context.IsFailure
            ? Result.Failure<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>(context.Error!)
            : await workStore.CreateRequestAsync(
                context.Value.Authorization, context.Value.Asset, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>> CreateRequestFromEventAsync(
        SessionSnapshot? session,
        CreateMaintenanceRequestFromEvent request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = await ResolveAssetMutationAsync(session, request.AssetId, cancellationToken).ConfigureAwait(false);
        if (context.IsFailure)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>(context.Error!);
        }

        var source = AuthorizeSource(session, request.Event);
        return source.IsFailure
            ? Result.Failure<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>(source.Error!)
            : await workStore.CreateRequestFromEventAsync(
                context.Value.Authorization, context.Value.Asset, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>> ApproveRequestAsync(
        SessionSnapshot? session,
        ApproveMaintenanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await workStore.ReadRequestAsync(request.RequestId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Failure<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>(
                "maintenance.request_not_found", "Maintenance request was not found.");
        }

        var authorization = SessionAuthorization.AuthorizeMutation(
            session, MaintenanceWorkPermissions.Manage(current.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>(authorization.Error!)
            : await workStore.ApproveRequestAsync(authorization.Value, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<MaintenanceWorkCommandResult<MaintenanceDefectSnapshot>>> CreateDefectAsync(
        SessionSnapshot? session,
        CreateMaintenanceDefect request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = await ResolveAssetMutationAsync(session, request.AssetId, cancellationToken).ConfigureAwait(false);
        return context.IsFailure
            ? Result.Failure<MaintenanceWorkCommandResult<MaintenanceDefectSnapshot>>(context.Error!)
            : await workStore.CreateDefectAsync(
                context.Value.Authorization, context.Value.Asset, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<MaintenanceWorkCommandResult<MaintenanceDefectSnapshot>>> ConfirmDefectAsync(
        SessionSnapshot? session,
        ConfirmMaintenanceDefect request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await workStore.ReadDefectAsync(request.DefectId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Failure<MaintenanceWorkCommandResult<MaintenanceDefectSnapshot>>(
                "maintenance.defect_not_found", "Maintenance defect was not found.");
        }

        var authorization = SessionAuthorization.AuthorizeMutation(
            session, MaintenanceWorkPermissions.Manage(current.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenanceWorkCommandResult<MaintenanceDefectSnapshot>>(authorization.Error!)
            : await workStore.ConfirmDefectAsync(authorization.Value, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> CreateWorkOrderAsync(
        SessionSnapshot? session,
        CreateWorkOrderFromRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = await workStore.ReadRequestAsync(request.RequestId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(
                "maintenance.request_not_found", "Maintenance request was not found.");
        }

        var authorization = SessionAuthorization.AuthorizeMutation(
            session, MaintenanceWorkPermissions.Manage(source.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(authorization.Error!)
            : await workStore.CreateWorkOrderAsync(authorization.Value, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> CreateWorkOrderAsync(
        SessionSnapshot? session,
        CreateWorkOrderFromDefect request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = await workStore.ReadDefectAsync(request.DefectId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(
                "maintenance.defect_not_found", "Maintenance defect was not found.");
        }

        var authorization = SessionAuthorization.AuthorizeMutation(
            session, MaintenanceWorkPermissions.Manage(source.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(authorization.Error!)
            : await workStore.CreateWorkOrderAsync(authorization.Value, request, cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> ClaimWorkOrderAsync(
        MaintenanceWorkUserContext? context,
        ClaimMaintenanceWorkOrder request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(context, request.WorkOrderId, (authorization, actor, token) =>
            workStore.ClaimWorkOrderAsync(authorization, actor, request, token), cancellationToken);

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> AcceptWorkOrderAsync(
        MaintenanceWorkUserContext? context,
        TransitionMaintenanceWorkOrder request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(context, request.WorkOrderId, (authorization, actor, token) =>
            workStore.AcceptWorkOrderAsync(authorization, actor, request, token), cancellationToken);

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> StartWorkOrderAsync(
        MaintenanceWorkUserContext? context,
        TransitionMaintenanceWorkOrder request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(context, request.WorkOrderId, (authorization, actor, token) =>
            workStore.StartWorkOrderAsync(authorization, actor, request, token), cancellationToken);

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> SubmitWorkOrderForAcceptanceAsync(
        MaintenanceWorkUserContext? context,
        TransitionMaintenanceWorkOrder request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(context, request.WorkOrderId, (authorization, actor, token) =>
            workStore.SubmitWorkOrderForAcceptanceAsync(
                authorization, actor, request, token), cancellationToken);

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> UpdateChecklistItemAsync(
        MaintenanceWorkUserContext? context,
        UpdateWorkOrderChecklistItem request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(context, request.WorkOrderId, (authorization, actor, token) =>
            workStore.UpdateChecklistItemAsync(authorization, actor, request, token), cancellationToken);

    public async Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> AcceptWorkResultAsync(
        SessionSnapshot? session,
        TransitionMaintenanceWorkOrder request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await workStore.ReadWorkOrderAsync(request.WorkOrderId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(
                "maintenance.work_order_not_found", "Work order was not found.");
        }

        var authorization = SessionAuthorization.AuthorizeMutation(
            session, MaintenanceWorkPermissions.Accept(current.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(authorization.Error!)
            : await workStore.AcceptWorkResultAsync(
                authorization.Value, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<MaintenanceWorkOrderSnapshot>> ReadWorkOrderAsync(
        SessionSnapshot? session,
        MaintenanceWorkOrderId workOrderId,
        CancellationToken cancellationToken = default)
    {
        var current = await workStore.ReadWorkOrderAsync(workOrderId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Failure<MaintenanceWorkOrderSnapshot>(
                "maintenance.work_order_not_found", "Work order was not found.");
        }

        var authorization = SessionAuthorization.AuthorizeAccess(
            session, MaintenancePermissions.Read(current.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenanceWorkOrderSnapshot>(authorization.Error!)
            : Result.Success(current);
    }

    public async Task<Result<MaintenanceRequestSnapshot>> ReadRequestAsync(
        SessionSnapshot? session,
        MaintenanceRequestId requestId,
        CancellationToken cancellationToken = default)
    {
        var current = await workStore.ReadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Failure<MaintenanceRequestSnapshot>(
                "maintenance.request_not_found", "Maintenance request was not found.");
        }

        var authorization = SessionAuthorization.AuthorizeAccess(
            session, MaintenancePermissions.Read(current.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenanceRequestSnapshot>(authorization.Error!)
            : Result.Success(current);
    }

    public async Task<Result<MaintenanceDefectSnapshot>> ReadDefectAsync(
        SessionSnapshot? session,
        MaintenanceDefectId defectId,
        CancellationToken cancellationToken = default)
    {
        var current = await workStore.ReadDefectAsync(defectId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Failure<MaintenanceDefectSnapshot>(
                "maintenance.defect_not_found", "Maintenance defect was not found.");
        }

        var authorization = SessionAuthorization.AuthorizeAccess(
            session, MaintenancePermissions.Read(current.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenanceDefectSnapshot>(authorization.Error!)
            : Result.Success(current);
    }

    public async Task<Result<MaintenanceRequestPage>> QueryRequestsAsync(
        SessionSnapshot? session,
        MaintenanceRequestQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ValidateQuery(
                query.PageSize,
                query.Search,
                query.AfterCreatedAt,
                query.AfterRequestId is not null) ||
            query.State is { } state && !Enum.IsDefined(state))
        {
            return Failure<MaintenanceRequestPage>(
                "maintenance.request_query", "Maintenance request query is invalid.");
        }

        var authorization = SessionAuthorization.AuthorizeAccess(
            session, MaintenancePermissions.Read(query.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenanceRequestPage>(authorization.Error!)
            : Result.Success(
                await workStore.QueryRequestsAsync(query, cancellationToken).ConfigureAwait(false));
    }

    public async Task<Result<MaintenanceDefectPage>> QueryDefectsAsync(
        SessionSnapshot? session,
        MaintenanceDefectQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ValidateQuery(
                query.PageSize,
                query.Search,
                query.AfterCreatedAt,
                query.AfterDefectId is not null) ||
            query.State is { } state && !Enum.IsDefined(state))
        {
            return Failure<MaintenanceDefectPage>(
                "maintenance.defect_query", "Maintenance defect query is invalid.");
        }

        var authorization = SessionAuthorization.AuthorizeAccess(
            session, MaintenancePermissions.Read(query.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenanceDefectPage>(authorization.Error!)
            : Result.Success(
                await workStore.QueryDefectsAsync(query, cancellationToken).ConfigureAwait(false));
    }

    public async Task<Result<MaintenanceWorkOrderPage>> QueryWorkOrdersAsync(
        SessionSnapshot? session,
        MaintenanceWorkOrderQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ValidateQuery(
                query.PageSize,
                query.Search,
                query.AfterCreatedAt,
                query.AfterWorkOrderId is not null) ||
            query.State is { } state && !Enum.IsDefined(state))
        {
            return Failure<MaintenanceWorkOrderPage>(
                "maintenance.work_order_query", "Maintenance work order query is invalid.");
        }

        var authorization = SessionAuthorization.AuthorizeAccess(
            session, MaintenancePermissions.Read(query.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenanceWorkOrderPage>(authorization.Error!)
            : Result.Success(
                await workStore.QueryWorkOrdersAsync(query, cancellationToken).ConfigureAwait(false));
    }

    public async Task<Result<MaintenanceOverviewSnapshot>> ReadOverviewAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(
            session, MaintenancePermissions.Read(scopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenanceOverviewSnapshot>(authorization.Error!)
            : Result.Success(await workStore.ReadOverviewAsync(
                scopeId,
                DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime),
                cancellationToken).ConfigureAwait(false));
    }

    public async Task<Result<MaintenanceEventSourceLink>> OpenRequestSourceAsync(
        SessionSnapshot? session,
        MaintenanceRequestId requestId,
        CancellationToken cancellationToken = default)
    {
        var request = await workStore.ReadRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        if (request?.EventSource is null)
        {
            return Failure<MaintenanceEventSourceLink>(
                "maintenance.source_not_found", "Maintenance request has no Event source.");
        }

        var read = SessionAuthorization.AuthorizeAccess(session, MaintenancePermissions.Read(request.ScopeId), clock);
        if (read.IsFailure)
        {
            return Result.Failure<MaintenanceEventSourceLink>(read.Error!);
        }

        foreach (var permission in request.EventSource.RequiredPermissions)
        {
            var authorization = SessionAuthorization.AuthorizeAccess(session, permission, clock);
            if (authorization.IsFailure)
            {
                return Result.Failure<MaintenanceEventSourceLink>(authorization.Error!);
            }
        }

        return Result.Success(request.EventSource);
    }

    private async Task<Result<AssetMutationContext>> ResolveAssetMutationAsync(
        SessionSnapshot? session,
        MaintenanceAssetId assetId,
        CancellationToken cancellationToken)
    {
        var asset = await assetStore.ReadAssetAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (asset is null)
        {
            return Failure<AssetMutationContext>("maintenance.asset_not_found", "Maintenance asset was not found.");
        }

        var authorization = SessionAuthorization.AuthorizeMutation(
            session, MaintenanceWorkPermissions.Manage(asset.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<AssetMutationContext>(authorization.Error!)
            : Result.Success(new AssetMutationContext(asset, authorization.Value));
    }

    private async Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> ExecuteAsync(
        MaintenanceWorkUserContext? context,
        MaintenanceWorkOrderId workOrderId,
        Func<AuthorizedMutation, Dispatcher.Workspace.PersonId, CancellationToken,
            Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>>> mutation,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(
                "permission.denied", "A person context is required for work execution.");
        }

        var current = await workStore.ReadWorkOrderAsync(workOrderId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(
                "maintenance.work_order_not_found", "Work order was not found.");
        }

        var authorization = SessionAuthorization.AuthorizeMutation(
            context.Session, MaintenanceWorkPermissions.Execute(current.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(authorization.Error!)
            : await mutation(authorization.Value, context.PersonId, cancellationToken).ConfigureAwait(false);
    }

    private Result AuthorizeSource(SessionSnapshot? session, OperationalEventRecord source)
    {
        foreach (var permission in new[]
        {
            PermissionCode.From("events.dispatcher.read"),
            PermissionCode.From($"runtime.point.p{source.PointId.Value:N}.read"),
        })
        {
            var authorization = SessionAuthorization.AuthorizeAccess(session, permission, clock);
            if (authorization.IsFailure)
            {
                return Result.Failure(authorization.Error!);
            }
        }

        return Result.Success();
    }

    private bool ValidateQuery(
        int pageSize,
        string? search,
        DateTimeOffset? afterCreatedAt,
        bool hasAfterId) =>
        pageSize > 0 &&
        pageSize <= limits.MaximumPageSize &&
        (search is null || search.Length <= 200) &&
        (afterCreatedAt is not null) == hasAfterId;

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));

    private sealed record AssetMutationContext(
        MaintenanceAssetSnapshot Asset,
        AuthorizedMutation Authorization);
}
