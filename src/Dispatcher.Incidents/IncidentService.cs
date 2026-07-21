using Dispatcher.Events;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Incidents;

public sealed class IncidentService
{
    private readonly IncidentStore store;
    private readonly IWallClock clock;

    public IncidentService(IncidentStore store, IWallClock clock)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<Result<IncidentCommandResult<IncidentSnapshot>>> CreateAsync(
        SessionSnapshot? session, CreateIncidentRequest request, CancellationToken cancellationToken = default)
    {
        var authorization = SessionAuthorization.AuthorizeMutation(session, IncidentPermissions.Create, clock);
        return authorization.IsFailure
            ? Result.Failure<IncidentCommandResult<IncidentSnapshot>>(authorization.Error!)
            : await store.CreateAsync(authorization.Value, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<IncidentCommandResult<IncidentSnapshot>>> LinkEventAsync(
        SessionSnapshot? session, LinkIncidentEventRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authorization = SessionAuthorization.AuthorizeMutation(session, IncidentPermissions.LinkSource, clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<IncidentCommandResult<IncidentSnapshot>>(authorization.Error!);
        }

        var sourceAuthorization = AuthorizeSource(session, request.Event);
        return sourceAuthorization.IsFailure
            ? Result.Failure<IncidentCommandResult<IncidentSnapshot>>(sourceAuthorization.Error!)
            : await store.LinkEventAsync(authorization.Value, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<IncidentCommandResult<IncidentTaskSnapshot>>> CreateTaskAsync(
        SessionSnapshot? session, CreateIncidentTaskRequest request, CancellationToken cancellationToken = default)
    {
        var authorization = SessionAuthorization.AuthorizeMutation(session, IncidentPermissions.ManageTask, clock);
        return authorization.IsFailure
            ? Result.Failure<IncidentCommandResult<IncidentTaskSnapshot>>(authorization.Error!)
            : await store.CreateTaskAsync(authorization.Value, request, cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<IncidentCommandResult<IncidentTaskSnapshot>>> AcceptTaskAsync(
        IncidentUserContext? context, TransitionIncidentTaskRequest request, CancellationToken cancellationToken = default) =>
        TransitionAsync(context, request, store.AcceptTaskAsync, cancellationToken);

    public Task<Result<IncidentCommandResult<IncidentTaskSnapshot>>> TransferTaskAsync(
        IncidentUserContext? context, TransitionIncidentTaskRequest request, CancellationToken cancellationToken = default) =>
        TransitionAsync(context, request, store.TransferTaskAsync, cancellationToken);

    public Task<Result<IncidentCommandResult<IncidentTaskSnapshot>>> ReturnTaskAsync(
        IncidentUserContext? context, TransitionIncidentTaskRequest request, CancellationToken cancellationToken = default) =>
        TransitionAsync(context, request, store.ReturnTaskAsync, cancellationToken);

    public async Task<Result<IncidentSnapshot>> ReadAsync(
        SessionSnapshot? session, IncidentId incidentId, CancellationToken cancellationToken = default)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(session, IncidentPermissions.Read, clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<IncidentSnapshot>(authorization.Error!);
        }

        var incident = await store.ReadIncidentAsync(incidentId, cancellationToken).ConfigureAwait(false);
        return incident is null
            ? Failure<IncidentSnapshot>("incident.not_found", "Incident was not found.")
            : Result.Success(incident);
    }

    public async Task<Result<IncidentSourceLink>> OpenSourceLinkAsync(
        SessionSnapshot? session, IncidentId incidentId, IncidentSourceLinkId linkId,
        CancellationToken cancellationToken = default)
    {
        var incident = await ReadAsync(session, incidentId, cancellationToken).ConfigureAwait(false);
        if (incident.IsFailure)
        {
            return Result.Failure<IncidentSourceLink>(incident.Error!);
        }

        var link = incident.Value.SourceLinks.SingleOrDefault(value => value.LinkId == linkId);
        if (link is null)
        {
            return Failure<IncidentSourceLink>("incident.source_not_found", "Incident source link was not found.");
        }

        foreach (var permission in link.RequiredPermissions)
        {
            var authorization = SessionAuthorization.AuthorizeAccess(session, permission, clock);
            if (authorization.IsFailure)
            {
                return Result.Failure<IncidentSourceLink>(authorization.Error!);
            }
        }

        return Result.Success(link);
    }

    private async Task<Result<IncidentCommandResult<IncidentTaskSnapshot>>> TransitionAsync(
        IncidentUserContext? context,
        TransitionIncidentTaskRequest request,
        Func<AuthorizedMutation, Workspace.PersonId, TransitionIncidentTaskRequest, CancellationToken,
            Task<Result<IncidentCommandResult<IncidentTaskSnapshot>>>> transition,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return Failure<IncidentCommandResult<IncidentTaskSnapshot>>(
                "permission.denied", "A person context is required for a task transition.");
        }

        var authorization = SessionAuthorization.AuthorizeMutation(
            context.Session, IncidentPermissions.TransitionTask, clock);
        return authorization.IsFailure
            ? Result.Failure<IncidentCommandResult<IncidentTaskSnapshot>>(authorization.Error!)
            : await transition(authorization.Value, context.PersonId, request, cancellationToken).ConfigureAwait(false);
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

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));
}
