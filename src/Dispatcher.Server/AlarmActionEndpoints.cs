using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Dispatcher.Server;

public interface IAlarmMaintenanceConstraintResolver
{
    AlarmMaintenanceConstraintSnapshot Resolve(AlarmOccurrenceId occurrenceId);
}

public sealed class UnrestrictedAlarmMaintenanceConstraintResolver : IAlarmMaintenanceConstraintResolver
{
    public AlarmMaintenanceConstraintSnapshot Resolve(AlarmOccurrenceId occurrenceId) =>
        AlarmMaintenanceConstraintSnapshot.Unrestricted(occurrenceId);
}

public sealed class AlarmActionCoordinator
{
    private readonly AlarmActionStore actions;
    private readonly EventStore events;
    private readonly IAlarmMaintenanceConstraintResolver constraints;
    private readonly IWallClock clock;

    public AlarmActionCoordinator(
        AlarmActionStore actions,
        EventStore events,
        IAlarmMaintenanceConstraintResolver constraints,
        IWallClock clock)
    {
        this.actions = actions;
        this.events = events;
        this.constraints = constraints;
        this.clock = clock;
    }

    public Task<Result<AlarmActionCompletionResult>> AcknowledgeAsync(
        SessionSnapshot? session,
        RuntimeScopeId scopeId,
        PointId pointId,
        AlarmOccurrenceId occurrenceId,
        StateVersion expectedVersion,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            session,
            pointId,
            occurrenceId,
            idempotencyKey,
            AlarmPermissions.Acknowledge,
            authorization => actions.AcknowledgeAsync(
                authorization,
                new AcknowledgeAlarmRequest(
                    scopeId,
                    pointId,
                    occurrenceId,
                    expectedVersion,
                    idempotencyKey,
                    constraints.Resolve(occurrenceId)),
                cancellationToken),
            cancellationToken);

    public Task<Result<AlarmActionCompletionResult>> AssignAsync(
        SessionSnapshot? session,
        RuntimeScopeId scopeId,
        PointId pointId,
        AlarmOccurrenceId occurrenceId,
        Guid assignedTo,
        StateVersion expectedVersion,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            session,
            pointId,
            occurrenceId,
            idempotencyKey,
            AlarmPermissions.Assign,
            authorization => actions.AssignAsync(
                authorization,
                new AssignAlarmRequest(
                    scopeId,
                    pointId,
                    occurrenceId,
                    assignedTo,
                    expectedVersion,
                    idempotencyKey,
                    constraints.Resolve(occurrenceId)),
                cancellationToken),
            cancellationToken);

    public Task<Result<AlarmActionCompletionResult>> ShelveAsync(
        SessionSnapshot? session,
        RuntimeScopeId scopeId,
        PointId pointId,
        AlarmOccurrenceId occurrenceId,
        DateTimeOffset shelvedUntil,
        string reason,
        StateVersion expectedVersion,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            session,
            pointId,
            occurrenceId,
            idempotencyKey,
            AlarmPermissions.Shelve,
            authorization => actions.ShelveAsync(
                authorization,
                new ShelveAlarmRequest(
                    scopeId,
                    pointId,
                    occurrenceId,
                    shelvedUntil,
                    reason,
                    expectedVersion,
                    idempotencyKey,
                    constraints.Resolve(occurrenceId)),
                cancellationToken),
            cancellationToken);

    private async Task<Result<AlarmActionCompletionResult>> ExecuteAsync(
        SessionSnapshot? session,
        PointId pointId,
        AlarmOccurrenceId occurrenceId,
        string idempotencyKey,
        PermissionCode permission,
        Func<AuthorizedMutation, Task<Result<AlarmActionResult>>> execute,
        CancellationToken cancellationToken)
    {
        var authorization = SessionAuthorization.AuthorizeMutation(session, permission, clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<AlarmActionCompletionResult>(authorization.Error!);
        }

        if (!authorization.Value.Session.Permissions.Allows(RuntimePermissions.ReadPoint(pointId)))
        {
            return Failure("permission.denied", "Point permission is required for Alarm action.");
        }

        AlarmActionResult action;
        try
        {
            var result = await execute(authorization.Value).ConfigureAwait(false);
            if (result.IsFailure)
            {
                return Result.Failure<AlarmActionCompletionResult>(result.Error!);
            }

            action = result.Value;
        }
        catch (TimeoutException)
        {
            return Result.Success(new AlarmActionCompletionResult(
                AlarmActionCompletion.Unknown,
                null,
                idempotencyKey));
        }

        var projected = await events.AcceptAlarmOccurrenceAsync(action.Occurrence, cancellationToken)
            .ConfigureAwait(false);
        if (projected.IsFailure)
        {
            return Result.Failure<AlarmActionCompletionResult>(projected.Error!);
        }

        return Result.Success(new AlarmActionCompletionResult(
            action.Disposition == AlarmActionDisposition.Applied
                ? AlarmActionCompletion.Applied
                : AlarmActionCompletion.Replayed,
            action.Occurrence,
            idempotencyKey));
    }

    private static Result<AlarmActionCompletionResult> Failure(string code, string message) =>
        Result.Failure<AlarmActionCompletionResult>(new OperationError(ErrorCode.From(code), message));
}

public static class AlarmActionEndpoints
{
    public static IServiceCollection AddAlarmActionsServer(
        this IServiceCollection services,
        string connectionString,
        string databaseRole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(sp => new AlarmActionStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            databaseRole,
            sp.GetRequiredService<IWallClock>()));
        services.TryAddSingleton<IAlarmMaintenanceConstraintResolver, UnrestrictedAlarmMaintenanceConstraintResolver>();
        services.AddSingleton<AlarmActionCoordinator>();
        return services;
    }

    public static IEndpointRouteBuilder MapAlarmActionsServer(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/alarms/{scopeId:guid}/{occurrenceId:guid}");
        group.MapPost("/acknowledge", AcknowledgeAsync);
        group.MapPost("/assign", AssignAsync);
        group.MapPost("/shelve", ShelveAsync);
        return endpoints;
    }

    private static async Task<IResult> AcknowledgeAsync(
        Guid scopeId,
        Guid occurrenceId,
        AlarmFacetActionRequest request,
        HttpContext context,
        RequestSessionResolver sessions,
        AlarmActionCoordinator actions,
        CancellationToken cancellationToken)
    {
        var result = await actions.AcknowledgeAsync(
            sessions.Resolve(context),
            RuntimeScopeId.From(scopeId),
            PointId.From(request.PointId),
            AlarmOccurrenceId.From(occurrenceId),
            StateVersion.From(request.ExpectedVersion),
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        return Response(result);
    }

    private static async Task<IResult> AssignAsync(
        Guid scopeId,
        Guid occurrenceId,
        AssignAlarmPayload request,
        HttpContext context,
        RequestSessionResolver sessions,
        AlarmActionCoordinator actions,
        CancellationToken cancellationToken)
    {
        var result = await actions.AssignAsync(
            sessions.Resolve(context),
            RuntimeScopeId.From(scopeId),
            PointId.From(request.PointId),
            AlarmOccurrenceId.From(occurrenceId),
            request.AssignedTo,
            StateVersion.From(request.ExpectedVersion),
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        return Response(result);
    }

    private static async Task<IResult> ShelveAsync(
        Guid scopeId,
        Guid occurrenceId,
        ShelveAlarmPayload request,
        HttpContext context,
        RequestSessionResolver sessions,
        AlarmActionCoordinator actions,
        CancellationToken cancellationToken)
    {
        var result = await actions.ShelveAsync(
            sessions.Resolve(context),
            RuntimeScopeId.From(scopeId),
            PointId.From(request.PointId),
            AlarmOccurrenceId.From(occurrenceId),
            request.ShelvedUntil,
            request.Reason,
            StateVersion.From(request.ExpectedVersion),
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        return Response(result);
    }

    private static IResult Response(Result<AlarmActionCompletionResult> result)
    {
        if (result.IsFailure)
        {
            return Results.Problem(
                statusCode: result.Error!.Code.Value switch
                {
                    "session.anonymous" or "session.revoked" or "session.expired" =>
                        StatusCodes.Status401Unauthorized,
                    "permission.denied" => StatusCodes.Status403Forbidden,
                    "alarm.occurrence_not_found" => StatusCodes.Status404NotFound,
                    "alarm.expected_version" or "alarm.idempotency_conflict" => StatusCodes.Status409Conflict,
                    _ => StatusCodes.Status400BadRequest,
                },
                title: result.Error.Code.Value,
                detail: result.Error.Message);
        }

        var value = result.Value;
        var links = value.Occurrence is null ? null : AlarmSourceLinkBuilder.From(value.Occurrence.PointId);
        var payload = new AlarmActionPayload(
            value.Completion.ToString(),
            value.IdempotencyKey,
            value.Occurrence?.OccurrenceId.Value,
            value.Occurrence?.PointId.Value,
            value.Occurrence?.Priority.ToString(),
            links?.DashboardBindingKey,
            links?.EquipmentHref);
        return value.Completion == AlarmActionCompletion.Unknown
            ? Results.Accepted(value: payload)
            : Results.Ok(payload);
    }
}

public sealed record AlarmFacetActionRequest(Guid PointId, ulong ExpectedVersion, string IdempotencyKey);

public sealed record AssignAlarmPayload(
    Guid PointId,
    Guid AssignedTo,
    ulong ExpectedVersion,
    string IdempotencyKey);

public sealed record ShelveAlarmPayload(
    Guid PointId,
    DateTimeOffset ShelvedUntil,
    string Reason,
    ulong ExpectedVersion,
    string IdempotencyKey);

public sealed record AlarmActionPayload(
    string Completion,
    string IdempotencyKey,
    Guid? OccurrenceId,
    Guid? PointId,
    string? Priority,
    string? DashboardBindingKey,
    string? EquipmentHref);

public sealed record AlarmSourceLinks(string DashboardBindingKey, string EquipmentHref);

public static class AlarmSourceLinkBuilder
{
    public static AlarmSourceLinks From(PointId pointId) => new(
        $"point:{pointId.Value:N}",
        $"/equipment?pointId={pointId.Value:D}");
}
