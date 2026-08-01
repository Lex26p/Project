using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Facilities;
using Dispatcher.Maintenance;
using Dispatcher.MyWork;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;

namespace Dispatcher.Server;

public sealed class MaintenanceEventApplicationService
{
    private readonly EventStore events;
    private readonly MaintenanceWorkService maintenance;
    private readonly IWallClock clock;

    public MaintenanceEventApplicationService(
        EventStore events,
        MaintenanceWorkService maintenance,
        IWallClock clock)
    {
        this.events = events;
        this.maintenance = maintenance;
        this.clock = clock;
    }

    public async Task<Result<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>> CreateRequestAsync(
        SessionSnapshot? session,
        RuntimeScopeId runtimeScopeId,
        OperationalEventId eventId,
        CreateMaintenanceRequestInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var authorization = SessionAuthorization.AuthorizeAccess(
            session, EventPermissions.ReadDispatcher, clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>(
                authorization.Error!);
        }

        var source = await events.ReadAsync(runtimeScopeId, eventId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>(
                new OperationError(ErrorCode.From("event.not_found"), "The source event was not found."));
        }

        return await maintenance.CreateRequestFromEventAsync(
            session,
            new CreateMaintenanceRequestFromEvent(
                MaintenanceRequestId.From(input.RequestId),
                MaintenanceAssetId.From(input.AssetId),
                input.Summary,
                source,
                input.IdempotencyKey),
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed class MaintenanceWorkApplicationService
{
    private readonly MaintenanceWorkService maintenance;
    private readonly MyWorkService myWork;
    private readonly WorkspaceStore workspace;
    private readonly IWallClock clock;

    public MaintenanceWorkApplicationService(
        MaintenanceWorkService maintenance,
        MyWorkService myWork,
        WorkspaceStore workspace,
        IWallClock clock)
    {
        this.maintenance = maintenance;
        this.myWork = myWork;
        this.workspace = workspace;
        this.clock = clock;
    }

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> ClaimAsync(
        SessionSnapshot? session,
        ClaimMaintenanceWorkOrder request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            session,
            request.WorkOrderId,
            context => maintenance.ClaimWorkOrderAsync(context, request, cancellationToken),
            cancellationToken);

    public async Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> CreateAsync(
        SessionSnapshot? session,
        CreateWorkOrderFromRequest request,
        CancellationToken cancellationToken) =>
        await SyncAsync(
            await maintenance.CreateWorkOrderAsync(
                session, request, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    public async Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> CreateAsync(
        SessionSnapshot? session,
        CreateWorkOrderFromDefect request,
        CancellationToken cancellationToken) =>
        await SyncAsync(
            await maintenance.CreateWorkOrderAsync(
                session, request, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> AcceptAsync(
        SessionSnapshot? session,
        TransitionMaintenanceWorkOrder request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            session,
            request.WorkOrderId,
            context => maintenance.AcceptWorkOrderAsync(context, request, cancellationToken),
            cancellationToken);

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> StartAsync(
        SessionSnapshot? session,
        TransitionMaintenanceWorkOrder request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            session,
            request.WorkOrderId,
            context => maintenance.StartWorkOrderAsync(context, request, cancellationToken),
            cancellationToken);

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> UpdateChecklistAsync(
        SessionSnapshot? session,
        UpdateWorkOrderChecklistItem request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            session,
            request.WorkOrderId,
            context => maintenance.UpdateChecklistItemAsync(context, request, cancellationToken),
            cancellationToken);

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> SubmitAsync(
        SessionSnapshot? session,
        TransitionMaintenanceWorkOrder request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            session,
            request.WorkOrderId,
            context => maintenance.SubmitWorkOrderForAcceptanceAsync(
                context, request, cancellationToken),
            cancellationToken);

    public async Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> AcceptResultAsync(
        SessionSnapshot? session,
        TransitionMaintenanceWorkOrder request,
        CancellationToken cancellationToken) =>
        await SyncAsync(
            await maintenance.AcceptWorkResultAsync(
                session, request, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    public Task<Result<MaintenanceWorkOrderSnapshot>> ReadAsync(
        SessionSnapshot? session,
        MaintenanceWorkOrderId workOrderId,
        CancellationToken cancellationToken) =>
        maintenance.ReadWorkOrderAsync(session, workOrderId, cancellationToken);

    public async Task<PersonId?> ResolvePersonAsync(
        SessionSnapshot? session,
        CancellationToken cancellationToken)
    {
        if (session is null)
        {
            return null;
        }

        var account = await workspace.FindAccountAsync(
            session.SubjectId, cancellationToken).ConfigureAwait(false);
        return account?.PersonId;
    }

    private async Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> ExecuteAsync(
        SessionSnapshot? session,
        MaintenanceWorkOrderId workOrderId,
        Func<MaintenanceWorkUserContext,
            Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>>> execute,
        CancellationToken cancellationToken)
    {
        var workOrder = await maintenance.ReadWorkOrderAsync(
            session, workOrderId, cancellationToken).ConfigureAwait(false);
        if (workOrder.IsFailure)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(
                workOrder.Error!);
        }

        var authorization = SessionAuthorization.AuthorizeAccess(
            session, MaintenanceWorkPermissions.Execute(workOrder.Value.ScopeId), clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(
                authorization.Error!);
        }

        var person = await ResolvePersonAsync(
            authorization.Value.Session, cancellationToken).ConfigureAwait(false);
        if (person is null)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(
                new OperationError(
                    ErrorCode.From("workspace.account_not_found"),
                    "Workspace account was not found for the session subject."));
        }

        return await SyncAsync(
            await execute(new MaintenanceWorkUserContext(
                authorization.Value.Session, person.Value)).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> SyncAsync(
        Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>> result,
        CancellationToken cancellationToken)
    {
        if (result.IsFailure)
        {
            return result;
        }

        var projection = await myWork.AcceptSourceAssignmentAsync(
            WorkAssignmentProjection.FromMaintenanceWorkOrder(result.Value.Value),
            cancellationToken).ConfigureAwait(false);
        return projection.IsFailure
            ? Result.Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(
                projection.Error!)
            : result;
    }
}

public static class MaintenanceWorkEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/overview", ReadOverviewAsync);
        group.MapGet("/requests", QueryRequestsAsync);
        group.MapGet("/requests/{requestId:guid}", ReadRequestAsync);
        group.MapPost("/requests", CreateRequestAsync);
        group.MapPost("/requests/{requestId:guid}/approve", ApproveRequestAsync);
        group.MapPost(
            "/events/{runtimeScopeId:guid}/{eventId:guid}/requests",
            CreateRequestFromEventAsync);
        group.MapGet("/defects", QueryDefectsAsync);
        group.MapGet("/defects/{defectId:guid}", ReadDefectAsync);
        group.MapPost("/defects", CreateDefectAsync);
        group.MapPost("/defects/{defectId:guid}/confirm", ConfirmDefectAsync);
        group.MapGet("/work-orders", QueryWorkOrdersAsync);
        group.MapGet("/work-orders/{workOrderId:guid}", ReadWorkOrderAsync);
        group.MapPost("/requests/{requestId:guid}/work-orders", CreateRequestWorkOrderAsync);
        group.MapPost("/defects/{defectId:guid}/work-orders", CreateDefectWorkOrderAsync);
        group.MapPost("/work-orders/{workOrderId:guid}/claim", ClaimWorkOrderAsync);
        group.MapPost("/work-orders/{workOrderId:guid}/accept", AcceptWorkOrderAsync);
        group.MapPost("/work-orders/{workOrderId:guid}/start", StartWorkOrderAsync);
        group.MapPut(
            "/work-orders/{workOrderId:guid}/checklist/{itemId:guid}",
            UpdateChecklistAsync);
        group.MapPost(
            "/work-orders/{workOrderId:guid}/submit-for-acceptance",
            SubmitWorkOrderAsync);
        group.MapPost(
            "/work-orders/{workOrderId:guid}/accept-result",
            AcceptWorkResultAsync);
    }

    private static async Task<IResult> ReadOverviewAsync(
        Guid scopeId,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkService maintenance,
        CancellationToken cancellationToken) =>
        ToHttpResult(
            await maintenance.ReadOverviewAsync(
                sessions.Resolve(context),
                FacilityScopeId.From(scopeId),
                cancellationToken).ConfigureAwait(false),
            value => new MaintenanceOverviewPayload(
                value.Overdue,
                value.DueToday,
                value.RequiresAssignment,
                value.InProgress,
                value.PendingAcceptance,
                value.SafetyAttention));

    private static async Task<IResult> QueryRequestsAsync(
        Guid scopeId,
        int pageSize,
        MaintenanceRequestState? state,
        Guid? assetId,
        string? search,
        DateTimeOffset? afterCreatedAt,
        Guid? afterRequestId,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkService maintenance,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        var result = await maintenance.QueryRequestsAsync(
            session,
            new MaintenanceRequestQuery(
                FacilityScopeId.From(scopeId),
                pageSize,
                state,
                assetId is null ? null : MaintenanceAssetId.From(assetId.Value),
                search,
                afterCreatedAt,
                afterRequestId is null
                    ? null
                    : MaintenanceRequestId.From(afterRequestId.Value)),
            cancellationToken).ConfigureAwait(false);
        return ToHttpResult(result, page => new MaintenanceRequestPagePayload(
            page.Requests.Select(item => ToPayload(item, session)).ToArray(),
            page.NextCreatedAt,
            page.NextRequestId?.Value));
    }

    private static async Task<IResult> ReadRequestAsync(
        Guid requestId,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkService maintenance,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        return ToHttpResult(
            await maintenance.ReadRequestAsync(
                session,
                MaintenanceRequestId.From(requestId),
                cancellationToken).ConfigureAwait(false),
            request => ToPayload(request, session));
    }

    private static async Task<IResult> CreateRequestAsync(
        CreateMaintenanceRequestInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkService maintenance,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        return ToHttpResult(
            await maintenance.CreateRequestAsync(
                session,
                new CreateMaintenanceRequest(
                    MaintenanceRequestId.From(input.RequestId),
                    MaintenanceAssetId.From(input.AssetId),
                    input.Summary,
                    input.IdempotencyKey),
                cancellationToken).ConfigureAwait(false),
            result => new MaintenanceRequestCommandPayload(
                ToPayload(result.Value, session),
                result.Disposition.ToString()));
    }

    private static async Task<IResult> CreateRequestFromEventAsync(
        Guid runtimeScopeId,
        Guid eventId,
        CreateMaintenanceRequestInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceEventApplicationService application,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        return ToHttpResult(
            await application.CreateRequestAsync(
                session,
                RuntimeScopeId.From(runtimeScopeId),
                OperationalEventId.From(eventId),
                input,
                cancellationToken).ConfigureAwait(false),
            result => new MaintenanceRequestCommandPayload(
                ToPayload(result.Value, session),
                result.Disposition.ToString()));
    }

    private static async Task<IResult> ApproveRequestAsync(
        Guid requestId,
        VersionedMaintenanceWorkInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkService maintenance,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        return ToHttpResult(
            await maintenance.ApproveRequestAsync(
                session,
                new ApproveMaintenanceRequest(
                    MaintenanceRequestId.From(requestId),
                    StateVersion.From(input.ExpectedVersion),
                    input.IdempotencyKey),
                cancellationToken).ConfigureAwait(false),
            result => new MaintenanceRequestCommandPayload(
                ToPayload(result.Value, session),
                result.Disposition.ToString()));
    }

    private static async Task<IResult> QueryDefectsAsync(
        Guid scopeId,
        int pageSize,
        MaintenanceDefectState? state,
        Guid? assetId,
        string? search,
        DateTimeOffset? afterCreatedAt,
        Guid? afterDefectId,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkService maintenance,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        var result = await maintenance.QueryDefectsAsync(
            session,
            new MaintenanceDefectQuery(
                FacilityScopeId.From(scopeId),
                pageSize,
                state,
                assetId is null ? null : MaintenanceAssetId.From(assetId.Value),
                search,
                afterCreatedAt,
                afterDefectId is null
                    ? null
                    : MaintenanceDefectId.From(afterDefectId.Value)),
            cancellationToken).ConfigureAwait(false);
        return ToHttpResult(result, page => new MaintenanceDefectPagePayload(
            page.Defects.Select(item => ToPayload(item, session)).ToArray(),
            page.NextCreatedAt,
            page.NextDefectId?.Value));
    }

    private static async Task<IResult> ReadDefectAsync(
        Guid defectId,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkService maintenance,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        return ToHttpResult(
            await maintenance.ReadDefectAsync(
                session,
                MaintenanceDefectId.From(defectId),
                cancellationToken).ConfigureAwait(false),
            defect => ToPayload(defect, session));
    }

    private static async Task<IResult> CreateDefectAsync(
        CreateMaintenanceDefectInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkService maintenance,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        return ToHttpResult(
            await maintenance.CreateDefectAsync(
                session,
                new CreateMaintenanceDefect(
                    MaintenanceDefectId.From(input.DefectId),
                    MaintenanceAssetId.From(input.AssetId),
                    input.Summary,
                    input.IdempotencyKey),
                cancellationToken).ConfigureAwait(false),
            result => new MaintenanceDefectCommandPayload(
                ToPayload(result.Value, session),
                result.Disposition.ToString()));
    }

    private static async Task<IResult> ConfirmDefectAsync(
        Guid defectId,
        VersionedMaintenanceWorkInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkService maintenance,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        return ToHttpResult(
            await maintenance.ConfirmDefectAsync(
                session,
                new ConfirmMaintenanceDefect(
                    MaintenanceDefectId.From(defectId),
                    StateVersion.From(input.ExpectedVersion),
                    input.IdempotencyKey),
                cancellationToken).ConfigureAwait(false),
            result => new MaintenanceDefectCommandPayload(
                ToPayload(result.Value, session),
                result.Disposition.ToString()));
    }

    private static async Task<IResult> QueryWorkOrdersAsync(
        Guid scopeId,
        int pageSize,
        MaintenanceWorkOrderState? state,
        Guid? assetId,
        Guid? assignedPersonId,
        string? search,
        DateTimeOffset? afterCreatedAt,
        Guid? afterWorkOrderId,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkService maintenance,
        MaintenanceWorkApplicationService application,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        var result = await maintenance.QueryWorkOrdersAsync(
            session,
            new MaintenanceWorkOrderQuery(
                FacilityScopeId.From(scopeId),
                pageSize,
                state,
                assetId is null ? null : MaintenanceAssetId.From(assetId.Value),
                assignedPersonId is null ? null : PersonId.From(assignedPersonId.Value),
                search,
                afterCreatedAt,
                afterWorkOrderId is null
                    ? null
                    : MaintenanceWorkOrderId.From(afterWorkOrderId.Value)),
            cancellationToken).ConfigureAwait(false);
        var person = result.IsSuccess
            ? await application.ResolvePersonAsync(session, cancellationToken).ConfigureAwait(false)
            : null;
        return ToHttpResult(result, page => new MaintenanceWorkOrderPagePayload(
            page.WorkOrders.Select(item => ToPayload(item, session, person)).ToArray(),
            page.NextCreatedAt,
            page.NextWorkOrderId?.Value));
    }

    private static async Task<IResult> ReadWorkOrderAsync(
        Guid workOrderId,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkService maintenance,
        MaintenanceWorkApplicationService application,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        var result = await maintenance.ReadWorkOrderAsync(
            session,
            MaintenanceWorkOrderId.From(workOrderId),
            cancellationToken).ConfigureAwait(false);
        var person = result.IsSuccess
            ? await application.ResolvePersonAsync(session, cancellationToken).ConfigureAwait(false)
            : null;
        return ToHttpResult(result, item => ToPayload(item, session, person));
    }

    private static async Task<IResult> CreateRequestWorkOrderAsync(
        Guid requestId,
        CreateMaintenanceWorkOrderInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkApplicationService application,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        var result = await application.CreateAsync(
            session,
            new CreateWorkOrderFromRequest(
                MaintenanceWorkOrderId.From(input.WorkOrderId),
                MaintenanceRequestId.From(requestId),
                input.Summary,
                PersonId.From(input.AssignedPersonId),
                input.ToSafety(),
                input.ToChecklist(),
                StateVersion.From(input.ExpectedSourceVersion),
                input.IdempotencyKey),
            cancellationToken).ConfigureAwait(false);
        var person = result.IsSuccess
            ? await application.ResolvePersonAsync(session, cancellationToken).ConfigureAwait(false)
            : null;
        return ToHttpResult(result, item => new MaintenanceWorkOrderCommandPayload(
            ToPayload(item.Value, session, person),
            item.Disposition.ToString()));
    }

    private static async Task<IResult> CreateDefectWorkOrderAsync(
        Guid defectId,
        CreateMaintenanceWorkOrderInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkApplicationService application,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        var result = await application.CreateAsync(
            session,
            new CreateWorkOrderFromDefect(
                MaintenanceWorkOrderId.From(input.WorkOrderId),
                MaintenanceDefectId.From(defectId),
                input.Summary,
                PersonId.From(input.AssignedPersonId),
                input.ToSafety(),
                input.ToChecklist(),
                StateVersion.From(input.ExpectedSourceVersion),
                input.IdempotencyKey),
            cancellationToken).ConfigureAwait(false);
        var person = result.IsSuccess
            ? await application.ResolvePersonAsync(session, cancellationToken).ConfigureAwait(false)
            : null;
        return ToHttpResult(result, item => new MaintenanceWorkOrderCommandPayload(
            ToPayload(item.Value, session, person),
            item.Disposition.ToString()));
    }

    private static Task<IResult> ClaimWorkOrderAsync(
        Guid workOrderId,
        VersionedMaintenanceWorkInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkApplicationService application,
        CancellationToken cancellationToken) =>
        ExecuteWorkOrderAsync(
            MaintenanceWorkOrderId.From(workOrderId),
            context,
            sessions,
            application,
            (session, token) => application.ClaimAsync(
                session,
                new ClaimMaintenanceWorkOrder(
                    MaintenanceWorkOrderId.From(workOrderId),
                    StateVersion.From(input.ExpectedVersion),
                    input.IdempotencyKey),
                token),
            cancellationToken);

    private static Task<IResult> AcceptWorkOrderAsync(
        Guid workOrderId,
        VersionedMaintenanceWorkInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkApplicationService application,
        CancellationToken cancellationToken) =>
        ExecuteWorkOrderAsync(
            MaintenanceWorkOrderId.From(workOrderId),
            context,
            sessions,
            application,
            (session, token) => application.AcceptAsync(
                session, input.ToTransition(workOrderId), token),
            cancellationToken);

    private static Task<IResult> StartWorkOrderAsync(
        Guid workOrderId,
        VersionedMaintenanceWorkInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkApplicationService application,
        CancellationToken cancellationToken) =>
        ExecuteWorkOrderAsync(
            MaintenanceWorkOrderId.From(workOrderId),
            context,
            sessions,
            application,
            (session, token) => application.StartAsync(
                session, input.ToTransition(workOrderId), token),
            cancellationToken);

    private static Task<IResult> SubmitWorkOrderAsync(
        Guid workOrderId,
        VersionedMaintenanceWorkInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkApplicationService application,
        CancellationToken cancellationToken) =>
        ExecuteWorkOrderAsync(
            MaintenanceWorkOrderId.From(workOrderId),
            context,
            sessions,
            application,
            (session, token) => application.SubmitAsync(
                session, input.ToTransition(workOrderId), token),
            cancellationToken);

    private static Task<IResult> AcceptWorkResultAsync(
        Guid workOrderId,
        VersionedMaintenanceWorkInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkApplicationService application,
        CancellationToken cancellationToken) =>
        ExecuteWorkOrderAsync(
            MaintenanceWorkOrderId.From(workOrderId),
            context,
            sessions,
            application,
            (session, token) => application.AcceptResultAsync(
                session, input.ToTransition(workOrderId), token),
            cancellationToken);

    private static Task<IResult> UpdateChecklistAsync(
        Guid workOrderId,
        Guid itemId,
        UpdateMaintenanceChecklistInput input,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkApplicationService application,
        CancellationToken cancellationToken) =>
        ExecuteWorkOrderAsync(
            MaintenanceWorkOrderId.From(workOrderId),
            context,
            sessions,
            application,
            (session, token) => application.UpdateChecklistAsync(
                session,
                new UpdateWorkOrderChecklistItem(
                    MaintenanceWorkOrderId.From(workOrderId),
                    WorkOrderChecklistItemId.From(itemId),
                    input.Completed,
                    StateVersion.From(input.ExpectedVersion),
                    input.IdempotencyKey),
                token),
            cancellationToken);

    private static async Task<IResult> ExecuteWorkOrderAsync(
        MaintenanceWorkOrderId workOrderId,
        HttpContext context,
        RequestSessionResolver sessions,
        MaintenanceWorkApplicationService application,
        Func<SessionSnapshot?, CancellationToken,
            Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>>> execute,
        CancellationToken cancellationToken)
    {
        var session = sessions.Resolve(context);
        var result = await execute(session, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure &&
            result.Error?.Code.Value == "maintenance.work_version")
        {
            var current = await application.ReadAsync(
                session, workOrderId, cancellationToken).ConfigureAwait(false);
            if (current.IsSuccess)
            {
                var currentPerson = await application.ResolvePersonAsync(
                    session, cancellationToken).ConfigureAwait(false);
                return Results.Conflict(new MaintenanceWorkOrderConflictPayload(
                    result.Error.Code.Value,
                    result.Error.Message,
                    ToPayload(current.Value, session, currentPerson)));
            }
        }

        var person = result.IsSuccess
            ? await application.ResolvePersonAsync(session, cancellationToken).ConfigureAwait(false)
            : null;
        return ToHttpResult(result, item => new MaintenanceWorkOrderCommandPayload(
            ToPayload(item.Value, session, person),
            item.Disposition.ToString()));
    }

    private static MaintenanceWorkOrderPayload ToPayload(
        MaintenanceWorkOrderSnapshot workOrder,
        SessionSnapshot? session,
        PersonId? person)
    {
        var canExecute = session?.Permissions.Allows(
            MaintenanceWorkPermissions.Execute(workOrder.ScopeId)) == true;
        var isAssigned = person is not null && workOrder.AssignedPersonId == person.Value;
        var mandatoryComplete = workOrder.Checklist.All(item =>
            !item.Mandatory || item.CompletedAt is not null);
        var source = MaintenanceCrossLinks.SourceFor(workOrder);
        var canOpenSource = source.RequiredPermissions.All(permission =>
            session?.Permissions.Allows(permission) == true);
        return new MaintenanceWorkOrderPayload(
            workOrder.WorkOrderId.Value,
            workOrder.AssetId.Value,
            workOrder.ScopeId.Value,
            workOrder.SourceKind.ToString(),
            workOrder.SourceId,
            workOrder.Summary,
            workOrder.AssignedPersonId.Value,
            workOrder.State.ToString(),
            workOrder.Version.Value,
            new MaintenanceSafetyPayload(
                workOrder.Safety.PermitRequired,
                workOrder.Safety.IsolationRequired,
                workOrder.Safety.Instructions,
                workOrder.Safety.AcknowledgedAt),
            workOrder.Checklist.Select(item => new MaintenanceChecklistItemPayload(
                item.ItemId.Value,
                item.Description,
                item.Mandatory,
                item.CompletedAt,
                item.CompletedBy?.Value)).ToArray(),
            $"/maintenance/assets/{workOrder.AssetId.Value:D}",
            canOpenSource ? source.Route : null,
            canExecute && workOrder.State == MaintenanceWorkOrderState.Overdue,
            canExecute && isAssigned && workOrder.State == MaintenanceWorkOrderState.Assigned,
            canExecute && isAssigned && workOrder.State == MaintenanceWorkOrderState.Accepted,
            canExecute && isAssigned &&
                (workOrder.State is MaintenanceWorkOrderState.Accepted or
                    MaintenanceWorkOrderState.InProgress),
            canExecute && isAssigned && mandatoryComplete &&
                workOrder.State == MaintenanceWorkOrderState.InProgress,
            session?.Permissions.Allows(
                MaintenanceWorkPermissions.Accept(workOrder.ScopeId)) == true &&
                workOrder.State == MaintenanceWorkOrderState.PendingAcceptance,
            workOrder.CreatedAt,
            workOrder.UpdatedAt);
    }

    private static MaintenanceRequestPayload ToPayload(
        MaintenanceRequestSnapshot request,
        SessionSnapshot? session)
    {
        var canManage = session?.Permissions.Allows(
            MaintenanceWorkPermissions.Manage(request.ScopeId)) == true;
        var canOpenSource = request.EventSource is { } source &&
            source.RequiredPermissions.All(permission =>
                session?.Permissions.Allows(permission) == true);
        return new MaintenanceRequestPayload(
            request.RequestId.Value,
            request.AssetId.Value,
            request.ScopeId.Value,
            request.Summary,
            request.State.ToString(),
            request.Version.Value,
            $"/maintenance/assets/{request.AssetId.Value:D}",
            canOpenSource ? request.EventSource!.Route : null,
            canManage && request.State == MaintenanceRequestState.Submitted,
            canManage && request.State == MaintenanceRequestState.Approved,
            request.CreatedAt,
            request.UpdatedAt);
    }

    private static MaintenanceDefectPayload ToPayload(
        MaintenanceDefectSnapshot defect,
        SessionSnapshot? session)
    {
        var canManage = session?.Permissions.Allows(
            MaintenanceWorkPermissions.Manage(defect.ScopeId)) == true;
        return new MaintenanceDefectPayload(
            defect.DefectId.Value,
            defect.AssetId.Value,
            defect.ScopeId.Value,
            defect.Summary,
            defect.State.ToString(),
            defect.Version.Value,
            $"/maintenance/assets/{defect.AssetId.Value:D}",
            canManage && defect.State == MaintenanceDefectState.Reported,
            canManage && defect.State == MaintenanceDefectState.Confirmed,
            defect.CreatedAt,
            defect.UpdatedAt);
    }

    private static IResult ToHttpResult<T, TPayload>(
        Result<T> result,
        Func<T, TPayload> map) =>
        result.IsSuccess ? Results.Ok(map(result.Value)) : Problem(result.Error!);

    private static IResult Problem(OperationError error) => Results.Problem(
        statusCode: error.Code.Value switch
        {
            "session.anonymous" or "session.expired" or "session.revoked" =>
                StatusCodes.Status401Unauthorized,
            "permission.denied" => StatusCodes.Status403Forbidden,
            "event.not_found" or "maintenance.asset_not_found" or
                "maintenance.request_not_found" or "maintenance.defect_not_found" or
                "maintenance.work_order_not_found" =>
                StatusCodes.Status404NotFound,
            "maintenance.request_query" or "maintenance.defect_query" or
                "maintenance.work_order_query" or
                "maintenance.request_invalid" or "maintenance.defect_invalid" or
                "maintenance.work_order_invalid" or "maintenance.command_invalid" =>
                StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status409Conflict,
        },
        title: error.Code.Value,
        detail: error.Message);
}

public sealed record CreateMaintenanceRequestInput(
    Guid RequestId,
    Guid AssetId,
    string Summary,
    string IdempotencyKey);

public sealed record CreateMaintenanceDefectInput(
    Guid DefectId,
    Guid AssetId,
    string Summary,
    string IdempotencyKey);

public sealed record CreateMaintenanceWorkOrderInput(
    Guid WorkOrderId,
    string Summary,
    Guid AssignedPersonId,
    bool PermitRequired,
    bool IsolationRequired,
    string? SafetyInstructions,
    IReadOnlyList<NewMaintenanceChecklistItemInput> Checklist,
    ulong ExpectedSourceVersion,
    string IdempotencyKey)
{
    public WorkOrderSafetyFields ToSafety() =>
        new(PermitRequired, IsolationRequired, SafetyInstructions, null);

    public IReadOnlyList<NewWorkOrderChecklistItem> ToChecklist() =>
        (Checklist ?? []).Select(item => new NewWorkOrderChecklistItem(
            WorkOrderChecklistItemId.From(item.ItemId),
            item.Description,
            item.Mandatory)).ToArray();
}

public sealed record NewMaintenanceChecklistItemInput(
    Guid ItemId,
    string Description,
    bool Mandatory);

public sealed record VersionedMaintenanceWorkInput(
    ulong ExpectedVersion,
    string IdempotencyKey,
    bool SafetyAcknowledged = false)
{
    public TransitionMaintenanceWorkOrder ToTransition(Guid workOrderId) => new(
        MaintenanceWorkOrderId.From(workOrderId),
        StateVersion.From(ExpectedVersion),
        IdempotencyKey,
        SafetyAcknowledged);
}

public sealed record UpdateMaintenanceChecklistInput(
    bool Completed,
    ulong ExpectedVersion,
    string IdempotencyKey);

public sealed record MaintenanceRequestPayload(
    Guid RequestId,
    Guid AssetId,
    Guid ScopeId,
    string Summary,
    string State,
    ulong Version,
    string AssetHref,
    string? EventHref,
    bool CanApprove,
    bool CanCreateWorkOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MaintenanceRequestPagePayload(
    IReadOnlyList<MaintenanceRequestPayload> Requests,
    DateTimeOffset? NextCreatedAt,
    Guid? NextRequestId);

public sealed record MaintenanceRequestCommandPayload(
    MaintenanceRequestPayload Request,
    string Disposition);

public sealed record MaintenanceDefectPayload(
    Guid DefectId,
    Guid AssetId,
    Guid ScopeId,
    string Summary,
    string State,
    ulong Version,
    string AssetHref,
    bool CanConfirm,
    bool CanCreateWorkOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MaintenanceDefectPagePayload(
    IReadOnlyList<MaintenanceDefectPayload> Defects,
    DateTimeOffset? NextCreatedAt,
    Guid? NextDefectId);

public sealed record MaintenanceDefectCommandPayload(
    MaintenanceDefectPayload Defect,
    string Disposition);

public sealed record MaintenanceWorkOrderPayload(
    Guid WorkOrderId,
    Guid AssetId,
    Guid ScopeId,
    string SourceKind,
    Guid SourceId,
    string Summary,
    Guid AssignedPersonId,
    string State,
    ulong Version,
    MaintenanceSafetyPayload Safety,
    IReadOnlyList<MaintenanceChecklistItemPayload> Checklist,
    string AssetHref,
    string? SourceHref,
    bool CanClaim,
    bool CanAccept,
    bool CanStart,
    bool CanUpdateChecklist,
    bool CanSubmitForAcceptance,
    bool CanAcceptResult,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MaintenanceSafetyPayload(
    bool PermitRequired,
    bool IsolationRequired,
    string? Instructions,
    DateTimeOffset? AcknowledgedAt);

public sealed record MaintenanceChecklistItemPayload(
    Guid ItemId,
    string Description,
    bool Mandatory,
    DateTimeOffset? CompletedAt,
    Guid? CompletedBy);

public sealed record MaintenanceWorkOrderPagePayload(
    IReadOnlyList<MaintenanceWorkOrderPayload> WorkOrders,
    DateTimeOffset? NextCreatedAt,
    Guid? NextWorkOrderId);

public sealed record MaintenanceOverviewPayload(
    int Overdue,
    int DueToday,
    int RequiresAssignment,
    int InProgress,
    int PendingAcceptance,
    int SafetyAttention);

public sealed record MaintenanceWorkOrderCommandPayload(
    MaintenanceWorkOrderPayload WorkOrder,
    string Disposition);

public sealed record MaintenanceWorkOrderConflictPayload(
    string Code,
    string Message,
    MaintenanceWorkOrderPayload Current);
