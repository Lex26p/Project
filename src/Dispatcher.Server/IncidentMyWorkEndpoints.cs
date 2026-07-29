using Dispatcher.Events;
using Dispatcher.Core;
using Dispatcher.Incidents;
using Dispatcher.MyWork;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Dispatcher.Server;

public sealed class IncidentMyWorkApplicationService
{
    private readonly EventStore events;
    private readonly IncidentService incidents;
    private readonly MyWorkService myWork;
    private readonly WorkspaceStore workspace;
    private readonly WorkspaceService workspaceService;
    private readonly IWallClock clock;

    public IncidentMyWorkApplicationService(
        EventStore events,
        IncidentService incidents,
        MyWorkService myWork,
        WorkspaceStore workspace,
        WorkspaceService workspaceService,
        IWallClock clock)
    {
        this.events = events;
        this.incidents = incidents;
        this.myWork = myWork;
        this.workspace = workspace;
        this.workspaceService = workspaceService;
        this.clock = clock;
    }

    public async Task<Result<CreateIncidentWorkflowResult>> CreateFromEventAsync(
        SessionSnapshot? session,
        RuntimeScopeId scopeId,
        OperationalEventId? eventId,
        Dispatcher.Alarm.AlarmOccurrenceId? occurrenceId,
        CreateIncidentFromEventInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var validation = ValidateCreate(input);
        if (validation.IsFailure)
        {
            return Result.Failure<CreateIncidentWorkflowResult>(validation.Error!);
        }

        var user = await ResolveUserAsync(session, IncidentPermissions.Create, cancellationToken)
            .ConfigureAwait(false);
        if (user.IsFailure)
        {
            return Result.Failure<CreateIncidentWorkflowResult>(user.Error!);
        }

        foreach (var permission in new[]
        {
            IncidentPermissions.LinkSource,
            EventPermissions.ReadDispatcher,
        }.Concat(input.CreateTask ? [IncidentPermissions.ManageTask] : []))
        {
            var authorization = SessionAuthorization.AuthorizeAccess(session, permission, clock);
            if (authorization.IsFailure)
            {
                return Result.Failure<CreateIncidentWorkflowResult>(authorization.Error!);
            }
        }

        var source = eventId is { } exact
            ? await events.ReadAsync(scopeId, exact, cancellationToken).ConfigureAwait(false)
            : await events.ReadLatestForOccurrenceAsync(scopeId, occurrenceId!.Value, cancellationToken)
                .ConfigureAwait(false);
        if (source is null)
        {
            return Failure<CreateIncidentWorkflowResult>("event.not_found", "The source event was not found.");
        }

        var coordinator = input.CoordinatorPersonId is { } coordinatorId
            ? PersonId.From(coordinatorId)
            : user.Value.Account.PersonId;
        var coordinatorAccess = await ValidateCandidateAsync(
            user.Value, coordinator, cancellationToken).ConfigureAwait(false);
        if (coordinatorAccess.IsFailure)
        {
            return Result.Failure<CreateIncidentWorkflowResult>(coordinatorAccess.Error!);
        }

        PersonId? assigned = null;
        if (input.CreateTask)
        {
            assigned = input.AssignedPersonId is { } assignedId
                ? PersonId.From(assignedId)
                : user.Value.Account.PersonId;
            var assignedAccess = await ValidateCandidateAsync(
                user.Value, assigned.Value, cancellationToken).ConfigureAwait(false);
            if (assignedAccess.IsFailure)
            {
                return Result.Failure<CreateIncidentWorkflowResult>(assignedAccess.Error!);
            }
        }

        var created = await incidents.CreateAsync(
            session,
            new CreateIncidentRequest(
                IncidentId.From(input.IncidentId),
                input.Summary,
                coordinator,
                $"{input.IdempotencyKey}:incident"),
            cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return Result.Failure<CreateIncidentWorkflowResult>(created.Error!);
        }

        var linked = await incidents.LinkEventAsync(
            session,
            new LinkIncidentEventRequest(
                created.Value.Value.IncidentId,
                source,
                created.Value.Value.Version,
                $"{input.IdempotencyKey}:source"),
            cancellationToken).ConfigureAwait(false);
        if (linked.IsFailure)
        {
            return Result.Failure<CreateIncidentWorkflowResult>(linked.Error!);
        }

        IncidentTaskSnapshot? task = null;
        if (input.CreateTask)
        {
            var taskResult = await incidents.CreateTaskAsync(
                session,
                new CreateIncidentTaskRequest(
                    IncidentTaskId.From(input.TaskId!.Value),
                    linked.Value.Value.IncidentId,
                    input.TaskSummary!,
                    assigned!.Value,
                    linked.Value.Value.Version,
                    $"{input.IdempotencyKey}:task",
                    input.DueAt),
                cancellationToken).ConfigureAwait(false);
            if (taskResult.IsFailure)
            {
                return Result.Failure<CreateIncidentWorkflowResult>(taskResult.Error!);
            }

            task = taskResult.Value.Value;
            var projection = await myWork.AcceptSourceAssignmentAsync(
                WorkAssignmentProjection.FromIncidentTask(task),
                cancellationToken).ConfigureAwait(false);
            if (projection.IsFailure)
            {
                return Result.Failure<CreateIncidentWorkflowResult>(projection.Error!);
            }
        }

        return Result.Success(new CreateIncidentWorkflowResult(
            FilterSummary(linked.Value.Value, user.Value.Authorization.Session),
            task));
    }

    public async Task<Result<IncidentSnapshot>> ReadIncidentAsync(
        SessionSnapshot? session,
        IncidentId incidentId,
        CancellationToken cancellationToken)
    {
        var result = await incidents.ReadAsync(session, incidentId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Result.Success(FilterSummary(result.Value, session!))
            : result;
    }

    public Task<Result<IncidentSourceLink>> OpenSourceAsync(
        SessionSnapshot? session,
        IncidentId incidentId,
        IncidentSourceLinkId linkId,
        CancellationToken cancellationToken) =>
        incidents.OpenSourceLinkAsync(session, incidentId, linkId, cancellationToken);

    public async Task<Result<IReadOnlyList<WorkAssignmentProjection>>> ReadMyWorkAsync(
        SessionSnapshot? session,
        CancellationToken cancellationToken)
    {
        var context = await ResolveUserAsync(session, MyWorkPermissions.Read, cancellationToken)
            .ConfigureAwait(false);
        return context.IsFailure
            ? Result.Failure<IReadOnlyList<WorkAssignmentProjection>>(context.Error!)
            : await myWork.ReadAsync(
                new MyWorkUserContext(context.Value.Authorization.Session, context.Value.Account.PersonId),
                cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<MyWorkCounters>> ReadMyWorkCountersAsync(
        SessionSnapshot? session,
        CancellationToken cancellationToken)
    {
        var context = await ResolveUserAsync(session, MyWorkPermissions.Read, cancellationToken)
            .ConfigureAwait(false);
        return context.IsFailure
            ? Result.Failure<MyWorkCounters>(context.Error!)
            : await myWork.ReadCountersAsync(
                new MyWorkUserContext(context.Value.Authorization.Session, context.Value.Account.PersonId),
                cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<WorkAssignmentProjection>> ReadTaskAsync(
        SessionSnapshot? session,
        IncidentTaskId taskId,
        CancellationToken cancellationToken)
    {
        var list = await ReadMyWorkAsync(session, cancellationToken).ConfigureAwait(false);
        if (list.IsFailure)
        {
            return Result.Failure<WorkAssignmentProjection>(list.Error!);
        }

        var task = list.Value.SingleOrDefault(item =>
            item.SourceOwner == "incidents" && item.SourceItemId == taskId.Value);
        return task is null
            ? Failure<WorkAssignmentProjection>("my_work.task_not_found", "The task is not available in My Work.")
            : Result.Success(task);
    }

    public async Task<Result<IncidentTaskSnapshot>> TransitionTaskAsync(
        SessionSnapshot? session,
        IncidentTaskId taskId,
        TransitionMyWorkTaskInput input,
        string action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var user = await ResolveUserAsync(session, IncidentPermissions.TransitionTask, cancellationToken)
            .ConfigureAwait(false);
        if (user.IsFailure)
        {
            return Result.Failure<IncidentTaskSnapshot>(user.Error!);
        }

        var visible = await myWork.ReadAsync(
            new MyWorkUserContext(user.Value.Authorization.Session, user.Value.Account.PersonId),
            cancellationToken).ConfigureAwait(false);
        if (visible.IsFailure || !visible.Value.Any(item =>
                item.SourceOwner == "incidents" && item.SourceItemId == taskId.Value))
        {
            return visible.IsFailure
                ? Result.Failure<IncidentTaskSnapshot>(visible.Error!)
                : Failure<IncidentTaskSnapshot>("my_work.task_not_found", "The task is not available in My Work.");
        }

        PersonId? transferTo = null;
        if (string.Equals(action, "transfer", StringComparison.Ordinal))
        {
            if (input.TransferToPersonId is not { } candidate)
            {
                return Failure<IncidentTaskSnapshot>(
                    "incident.task_transfer", "Transfer target is required.");
            }

            transferTo = PersonId.From(candidate);
            var candidateAccess = await ValidateCandidateAsync(user.Value, transferTo.Value, cancellationToken)
                .ConfigureAwait(false);
            if (candidateAccess.IsFailure)
            {
                return Result.Failure<IncidentTaskSnapshot>(candidateAccess.Error!);
            }
        }

        var request = new TransitionIncidentTaskRequest(
            taskId,
            StateVersion.From(input.ExpectedVersion),
            input.IdempotencyKey,
            transferTo,
            input.Reason);
        var context = new IncidentUserContext(user.Value.Authorization.Session, user.Value.Account.PersonId);
        var transitioned = action switch
        {
            "accept" => await incidents.AcceptTaskAsync(context, request, cancellationToken).ConfigureAwait(false),
            "transfer" => await incidents.TransferTaskAsync(context, request, cancellationToken).ConfigureAwait(false),
            "return" => await incidents.ReturnTaskAsync(context, request, cancellationToken).ConfigureAwait(false),
            _ => Failure<IncidentCommandResult<IncidentTaskSnapshot>>(
                "incident.task_action", "Task action is not supported."),
        };
        if (transitioned.IsFailure)
        {
            return Result.Failure<IncidentTaskSnapshot>(transitioned.Error!);
        }

        var projection = await myWork.AcceptSourceAssignmentAsync(
            WorkAssignmentProjection.FromIncidentTask(transitioned.Value.Value),
            cancellationToken).ConfigureAwait(false);
        return projection.IsFailure
            ? Result.Failure<IncidentTaskSnapshot>(projection.Error!)
            : Result.Success(transitioned.Value.Value);
    }

    public async Task<Result<IReadOnlyList<TransferCandidate>>> FindTransferCandidatesAsync(
        SessionSnapshot? session,
        string query,
        CancellationToken cancellationToken)
    {
        var user = await ResolveUserAsync(session, MyWorkPermissions.Read, cancellationToken).ConfigureAwait(false);
        if (user.IsFailure)
        {
            return Result.Failure<IReadOnlyList<TransferCandidate>>(user.Error!);
        }

        var search = await workspaceService.SearchAsync(session, query, cancellationToken).ConfigureAwait(false);
        if (search.IsFailure)
        {
            return Result.Failure<IReadOnlyList<TransferCandidate>>(search.Error!);
        }

        var candidates = new List<TransferCandidate>();
        foreach (var item in search.Value.Where(item => item.Kind == "person").Take(20))
        {
            var profile = await workspaceService.ReadUserAsync(
                session, AccountId.From(item.Id), cancellationToken).ConfigureAwait(false);
            if (profile.IsSuccess)
            {
                candidates.Add(new TransferCandidate(
                    profile.Value.PersonId,
                    profile.Value.DisplayName,
                    profile.Value.Title));
            }
        }

        return Result.Success<IReadOnlyList<TransferCandidate>>(candidates);
    }

    private async Task<Result<ResolvedIncidentUser>> ResolveUserAsync(
        SessionSnapshot? session,
        PermissionCode permission,
        CancellationToken cancellationToken)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(session, permission, clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<ResolvedIncidentUser>(authorization.Error!);
        }

        var account = await workspace.FindAccountAsync(
            authorization.Value.Session.SubjectId,
            cancellationToken).ConfigureAwait(false);
        return account is null
            ? Failure<ResolvedIncidentUser>(
                "workspace.account_not_found",
                "Workspace account was not found for the session subject.")
            : Result.Success(new ResolvedIncidentUser(authorization.Value, account));
    }

    private async Task<Result> ValidateCandidateAsync(
        ResolvedIncidentUser user,
        PersonId candidate,
        CancellationToken cancellationToken)
    {
        if (candidate == user.Account.PersonId)
        {
            return Result.Success();
        }

        var profilePermission = SessionAuthorization.AuthorizeAccess(
            user.Authorization.Session, WorkspacePermissions.ProfileUserRead, clock);
        if (profilePermission.IsFailure)
        {
            return Result.Failure(profilePermission.Error!);
        }

        var profile = await workspace.ReadVisibleProfileByPersonAsync(
            user.Account.AccountId,
            candidate,
            cancellationToken).ConfigureAwait(false);
        return profile is null
            ? Failure("workspace.profile_not_found", "The selected person is not an allowed candidate.")
            : Result.Success();
    }

    private static Result ValidateCreate(CreateIncidentFromEventInput input)
    {
        if (input.IncidentId == Guid.Empty ||
            string.IsNullOrWhiteSpace(input.Summary) ||
            string.IsNullOrWhiteSpace(input.IdempotencyKey))
        {
            return Failure("incident.request_invalid", "Incident identity, summary and idempotency key are required.");
        }

        if (input.CreateTask &&
            (input.TaskId is null || input.TaskId == Guid.Empty ||
             string.IsNullOrWhiteSpace(input.TaskSummary)))
        {
            return Failure("incident.task_invalid", "Optional task identity and summary are required.");
        }

        if (input.DueAt is { } due && due.Offset != TimeSpan.Zero)
        {
            return Failure("incident.task_due", "Task due time must use UTC.");
        }

        return Result.Success();
    }

    private static IncidentSnapshot FilterSummary(IncidentSnapshot value, SessionSnapshot session) =>
        value with
        {
            SourceLinks = value.SourceLinks
                .Where(link => link.RequiredPermissions.All(session.Permissions.Allows))
                .ToArray(),
        };

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));

    private sealed record ResolvedIncidentUser(
        AuthorizedAccess Authorization,
        WorkspaceAccount Account);
}

public static class IncidentMyWorkEndpoints
{
    public static IServiceCollection AddIncidentMyWorkServer(
        this IServiceCollection services,
        string connectionString,
        string incidentDatabaseRole,
        string myWorkDatabaseRole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(incidentDatabaseRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(myWorkDatabaseRole);
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(sp => new IncidentStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            incidentDatabaseRole,
            sp.GetRequiredService<IWallClock>()));
        services.AddSingleton<IncidentService>();
        services.AddSingleton(sp => new MyWorkStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            myWorkDatabaseRole));
        services.AddSingleton<MyWorkService>();
        services.AddSingleton<IncidentMyWorkApplicationService>();
        return services;
    }

    public static IEndpointRouteBuilder MapIncidentMyWorkServer(this IEndpointRouteBuilder endpoints)
    {
        var events = endpoints.MapGroup("/api/events");
        events.MapPost("/{scopeId:guid}/{eventId:guid}/incident", async (
            Guid scopeId,
            Guid eventId,
            CreateIncidentFromEventInput input,
            HttpContext context,
            RequestSessionResolver sessions,
            IncidentMyWorkApplicationService application,
            CancellationToken cancellationToken) =>
            ToHttpResult(
                await application.CreateFromEventAsync(
                    sessions.Resolve(context),
                    RuntimeScopeId.From(scopeId),
                    OperationalEventId.From(eventId),
                    null,
                    input,
                    cancellationToken).ConfigureAwait(false),
                ToPayload));
        events.MapPost("/{scopeId:guid}/occurrences/{occurrenceId:guid}/incident", async (
            Guid scopeId,
            Guid occurrenceId,
            CreateIncidentFromEventInput input,
            HttpContext context,
            RequestSessionResolver sessions,
            IncidentMyWorkApplicationService application,
            CancellationToken cancellationToken) =>
            ToHttpResult(
                await application.CreateFromEventAsync(
                    sessions.Resolve(context),
                    RuntimeScopeId.From(scopeId),
                    null,
                    Dispatcher.Alarm.AlarmOccurrenceId.From(occurrenceId),
                    input,
                    cancellationToken).ConfigureAwait(false),
                ToPayload));

        var incidents = endpoints.MapGroup("/api/incidents");
        incidents.MapGet("/{incidentId:guid}", async (
            Guid incidentId,
            HttpContext context,
            RequestSessionResolver sessions,
            IncidentMyWorkApplicationService application,
            CancellationToken cancellationToken) =>
            ToHttpResult(
                await application.ReadIncidentAsync(
                    sessions.Resolve(context),
                    IncidentId.From(incidentId),
                    cancellationToken).ConfigureAwait(false),
                ToPayload));
        incidents.MapGet("/{incidentId:guid}/sources/{linkId:guid}", async (
            Guid incidentId,
            Guid linkId,
            HttpContext context,
            RequestSessionResolver sessions,
            IncidentMyWorkApplicationService application,
            CancellationToken cancellationToken) =>
            ToHttpResult(
                await application.OpenSourceAsync(
                    sessions.Resolve(context),
                    IncidentId.From(incidentId),
                    IncidentSourceLinkId.From(linkId),
                    cancellationToken).ConfigureAwait(false),
                link => new IncidentSourceLinkPayload(
                    link.LinkId.Value,
                    link.EventId.Value,
                    link.OccurrenceId.Value,
                    link.ScopeId.Value,
                    link.PointId.Value,
                    link.Route)));

        var work = endpoints.MapGroup("/api/my-work");
        work.MapGet("/", async (
            HttpContext context,
            RequestSessionResolver sessions,
            IncidentMyWorkApplicationService application,
            CancellationToken cancellationToken) =>
            ToHttpResult(
                await application.ReadMyWorkAsync(
                    sessions.Resolve(context),
                    cancellationToken).ConfigureAwait(false),
                values => values.Select(ToPayload).ToArray()));
        work.MapGet("/counters", async (
            HttpContext context,
            RequestSessionResolver sessions,
            IncidentMyWorkApplicationService application,
            CancellationToken cancellationToken) =>
            ToHttpResult(await application.ReadMyWorkCountersAsync(
                sessions.Resolve(context),
                cancellationToken).ConfigureAwait(false)));
        work.MapGet("/tasks/{taskId:guid}", async (
            Guid taskId,
            HttpContext context,
            RequestSessionResolver sessions,
            IncidentMyWorkApplicationService application,
            CancellationToken cancellationToken) =>
            ToHttpResult(
                await application.ReadTaskAsync(
                    sessions.Resolve(context),
                    IncidentTaskId.From(taskId),
                    cancellationToken).ConfigureAwait(false),
                ToPayload));
        work.MapGet("/transfer-candidates", async (
            string? query,
            HttpContext context,
            RequestSessionResolver sessions,
            IncidentMyWorkApplicationService application,
            CancellationToken cancellationToken) =>
            ToHttpResult(
                await application.FindTransferCandidatesAsync(
                    sessions.Resolve(context),
                    query ?? string.Empty,
                    cancellationToken).ConfigureAwait(false),
                values => values.Select(value => new TransferCandidatePayload(
                    value.PersonId.Value,
                    value.DisplayName,
                    value.Title)).ToArray()));
        work.MapPost("/tasks/{taskId:guid}/{action}", async (
            Guid taskId,
            string action,
            TransitionMyWorkTaskInput input,
            HttpContext context,
            RequestSessionResolver sessions,
            IncidentMyWorkApplicationService application,
            CancellationToken cancellationToken) =>
            ToHttpResult(
                await application.TransitionTaskAsync(
                    sessions.Resolve(context),
                    IncidentTaskId.From(taskId),
                    input,
                    action,
                    cancellationToken).ConfigureAwait(false),
                ToPayload));
        return endpoints;
    }

    private static IncidentSummaryPayload ToPayload(IncidentSnapshot value) => new(
        value.IncidentId.Value,
        value.Summary,
        value.CoordinatorPersonId.Value,
        value.Version.Value,
        value.CreatedAt,
        value.UpdatedAt,
        value.SourceLinks.Select(link => new IncidentSourceLinkPayload(
            link.LinkId.Value,
            link.EventId.Value,
            link.OccurrenceId.Value,
            link.ScopeId.Value,
            link.PointId.Value,
            link.Route)).ToArray());

    private static CreateIncidentWorkflowPayload ToPayload(CreateIncidentWorkflowResult value) => new(
        ToPayload(value.Incident),
        value.Task is null ? null : ToPayload(value.Task));

    private static IncidentTaskPayload ToPayload(IncidentTaskSnapshot value) => new(
        value.TaskId.Value,
        value.IncidentId.Value,
        value.Summary,
        value.AssignedPersonId.Value,
        value.State.ToString(),
        value.Version.Value,
        value.DueAt,
        value.LastTransitionReason,
        value.UpdatedAt);

    private static MyWorkItemPayload ToPayload(WorkAssignmentProjection value) => new(
        value.SourceOwner,
        value.SourceKind,
        value.SourceItemId,
        value.SourceVersion.Value,
        value.AssignedPersonId.Value,
        value.Summary,
        value.State,
        value.Route,
        value.DueAt,
        value.LastTransitionReason,
        value.UpdatedAt);

    private static IResult ToHttpResult(Result result) =>
        result.IsSuccess ? Results.NoContent() : Problem(result.Error!);

    private static IResult ToHttpResult<T>(Result<T> result) =>
        result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);

    private static IResult ToHttpResult<T, TPayload>(Result<T> result, Func<T, TPayload> map) =>
        result.IsSuccess ? Results.Ok(map(result.Value)) : Problem(result.Error!);

    private static IResult Problem(OperationError error) => Results.Problem(
        statusCode: error.Code.Value switch
        {
            "session.anonymous" or "session.expired" or "session.revoked" => StatusCodes.Status401Unauthorized,
            "permission.denied" => StatusCodes.Status403Forbidden,
            "event.not_found" or "incident.not_found" or "incident.source_not_found" or
                "incident.task_not_found" or "my_work.task_not_found" or
                "workspace.account_not_found" or "workspace.profile_not_found" => StatusCodes.Status404NotFound,
            "incident.request_invalid" or "incident.task_invalid" or "incident.task_due" or
                "incident.task_reason" or "incident.task_transfer" or "incident.task_action" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status409Conflict,
        },
        title: error.Code.Value,
        detail: error.Message);
}

public sealed record CreateIncidentFromEventInput(
    Guid IncidentId,
    string Summary,
    Guid? CoordinatorPersonId,
    bool CreateTask,
    Guid? TaskId,
    string? TaskSummary,
    Guid? AssignedPersonId,
    DateTimeOffset? DueAt,
    string IdempotencyKey);

public sealed record TransitionMyWorkTaskInput(
    ulong ExpectedVersion,
    string IdempotencyKey,
    Guid? TransferToPersonId,
    string? Reason);

public sealed record CreateIncidentWorkflowResult(
    IncidentSnapshot Incident,
    IncidentTaskSnapshot? Task);

public sealed record TransferCandidate(
    PersonId PersonId,
    string DisplayName,
    string? Title);

public sealed record IncidentSummaryPayload(
    Guid IncidentId,
    string Summary,
    Guid CoordinatorPersonId,
    ulong Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<IncidentSourceLinkPayload> Sources);

public sealed record IncidentSourceLinkPayload(
    Guid LinkId,
    Guid EventId,
    Guid OccurrenceId,
    Guid ScopeId,
    Guid PointId,
    string Route);

public sealed record IncidentTaskPayload(
    Guid TaskId,
    Guid IncidentId,
    string Summary,
    Guid AssignedPersonId,
    string State,
    ulong Version,
    DateTimeOffset? DueAt,
    string? LastTransitionReason,
    DateTimeOffset UpdatedAt);

public sealed record CreateIncidentWorkflowPayload(
    IncidentSummaryPayload Incident,
    IncidentTaskPayload? Task);

public sealed record MyWorkItemPayload(
    string SourceOwner,
    string SourceKind,
    Guid SourceItemId,
    ulong SourceVersion,
    Guid AssignedPersonId,
    string Summary,
    string State,
    string Route,
    DateTimeOffset? DueAt,
    string? LastTransitionReason,
    DateTimeOffset UpdatedAt);

public sealed record TransferCandidatePayload(
    Guid PersonId,
    string DisplayName,
    string? Title);
