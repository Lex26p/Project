using Dispatcher.Command;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Facilities;
using Dispatcher.Identity;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Simulator;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Dispatcher.Server;

public sealed record AcquireControlLeasePayload(Guid LeaseId, Guid ScopeId, int LifetimeSeconds, string? StepUpPassword);
public sealed record RevokeControlLeasePayload(string Reason);
public sealed record PrepareCommandPayload(
    Guid IntentId, Guid LeaseId, Guid ScopeId, Guid PointId, long DesiredValue, string Unit,
    int InteractionMode, Guid ExpectedRevisionId, ulong ExpectedRevisionNumber,
    long ExpectedGeneration, string ExpectedManifestFingerprint, ulong ExpectedCurrentPosition);
public sealed record CommandContextPayload(
    Guid ScopeId, Guid PointId, Guid RevisionId, ulong RevisionNumber, long Generation,
    string ManifestFingerprint, ulong CurrentPosition, long CurrentValue, string Unit,
    string Quality, string Freshness);
public sealed record ExecuteCommandPayload(Guid ExecutionId, Guid IntentId, Guid ScopeId, Guid PointId);
public sealed record ReconcileCommandPayload(Guid ScopeId, Guid PointId);
public sealed record CommandExecutionPayload(
    Guid ExecutionId, Guid IntentId, Guid LeaseId, Guid ScopeId, Guid PointId,
    string State, byte Progress, long? ResultValue, string? RejectionCode,
    DateTimeOffset AcceptedAt, DateTimeOffset UpdatedAt, DateTimeOffset? CompletedAt,
    ulong Version, string Disposition);
public sealed record CommandExecutionTransitionPayload(
    ulong Position, Guid ExecutionId, Guid PointId, string State, byte Progress,
    long? ResultValue, string? RejectionCode, DateTimeOffset OccurredAt, ulong Version);
public sealed record CommandExecutionSnapshotPayload(
    ulong Cursor, IReadOnlyList<CommandExecutionPayload> Executions);
public sealed record CommandExecutionFeedPayload(
    string Kind, ulong From, ulong To, IReadOnlyList<CommandExecutionTransitionPayload> Transitions);

internal sealed record CommandRealtimeSubscription(SessionId SessionId, RuntimeScopeId ScopeId, ulong Cursor);

public sealed class CommandRealtimeSubscriptionStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CommandRealtimeSubscription> subscriptions = new();
    internal void Set(string connectionId, CommandRealtimeSubscription value) => subscriptions[connectionId] = value;
    internal bool TryGet(string connectionId, out CommandRealtimeSubscription? value) => subscriptions.TryGetValue(connectionId, out value);
    internal void Remove(string connectionId) => subscriptions.TryRemove(connectionId, out _);
}

public sealed class CommandRealtimeHub(
    RequestSessionResolver sessions, CommandExecutionStore executions,
    CommandRealtimeSubscriptionStore subscriptions) : Hub
{
    public async Task<CommandExecutionSnapshotPayload> Bootstrap(Guid scopeId)
    {
        var session = sessions.Resolve(Context.GetHttpContext());
        var result = await executions.ReadSnapshotAsync(
            session, RuntimeScopeId.From(scopeId), Context.ConnectionAborted).ConfigureAwait(false);
        if (result.IsFailure) throw new HubException(result.Error!.Code.Value);
        subscriptions.Set(Context.ConnectionId, new(
            session!.Id, RuntimeScopeId.From(scopeId), result.Value.Cursor));
        return new(result.Value.Cursor, result.Value.Executions.Select(CommandEndpoints.ToPayload).ToArray());
    }

    public async Task<CommandExecutionFeedPayload> Poll(Guid scopeId, ulong cursor)
    {
        var session = sessions.Resolve(Context.GetHttpContext());
        if (!subscriptions.TryGet(Context.ConnectionId, out var subscription) || session is null ||
            subscription!.SessionId != session.Id || subscription.ScopeId != RuntimeScopeId.From(scopeId) ||
            subscription.Cursor != cursor)
        {
            subscriptions.Remove(Context.ConnectionId);
            return new("PermissionInvalidated", cursor, cursor, []);
        }
        var result = await executions.ReadFeedAsync(session, subscription.ScopeId, cursor, Context.ConnectionAborted)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            subscriptions.Remove(Context.ConnectionId);
            return new("PermissionInvalidated", cursor, cursor, []);
        }
        subscriptions.Set(Context.ConnectionId, subscription with { Cursor = result.Value.To });
        return new("Delta", result.Value.From, result.Value.To, result.Value.Transitions.Select(value => new CommandExecutionTransitionPayload(
            value.Position, value.ExecutionId.Value, value.PointId.Value, value.State.ToString(), value.Progress,
            value.ResultValue, value.RejectionCode, value.OccurredAt, value.Version.Value)).ToArray());
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

public static class CommandEndpoints
{
    public static IServiceCollection AddCommandServer(
        this IServiceCollection services, string connectionString, string commandRole, string simulatorRole,
        CommandSecurityPolicy policy)
    {
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(policy);
        services.AddSingleton(sp => new CommandStore(
            sp.GetRequiredService<NpgsqlDataSource>(), commandRole,
            sp.GetRequiredService<IWallClock>(), policy));
        services.AddSingleton(sp => new CommandExecutionStore(
            sp.GetRequiredService<NpgsqlDataSource>(), commandRole,
            sp.GetRequiredService<IWallClock>(), sp.GetService<SimulatorCommandCommitHook>()));
        services.AddSingleton<CommandRealtimeSubscriptionStore>();
        services.AddSingleton(sp => new SimulatorRuntimeStore(
            sp.GetRequiredService<NpgsqlDataSource>(), simulatorRole,
            sp.GetRequiredService<IWallClock>()));
        return services;
    }

    public static IEndpointRouteBuilder MapCommandServer(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/commands");
        group.MapGet("/context/{scopeId:guid}/{pointId:guid}", ReadContextAsync);
        group.MapPost("/leases", AcquireLeaseAsync);
        group.MapPost("/leases/{leaseId:guid}/revoke", RevokeLeaseAsync);
        group.MapPost("/intents/prepare", PrepareAsync);
        group.MapPost("/executions", ExecuteAsync);
        group.MapPost("/executions/{executionId:guid}/reconcile", ReconcileAsync);
        endpoints.MapHub<CommandRealtimeHub>("/hubs/commands");
        return endpoints;
    }

    private static async Task<IResult> ReadContextAsync(
        Guid scopeId, Guid pointId, HttpContext context, RequestSessionResolver sessions,
        IWallClock clock, SimulatorRuntimeStore simulator, RuntimeRegistry registry, CancellationToken token)
    {
        var scope = RuntimeScopeId.From(scopeId);
        var point = PointId.From(pointId);
        var authorization = SessionAuthorization.AuthorizeAccess(sessions.Resolve(context), CommandPermissions.Prepare, clock);
        if (authorization.IsFailure) return Problem(authorization.Error!);
        if (!authorization.Value.Session.Permissions.Allows(CommandPermissions.ControlScope(scope)) ||
            !authorization.Value.Session.Permissions.Allows(CommandPermissions.ControlPoint(point)))
            return Problem(new OperationError(ErrorCode.From("permission.denied"), "Exact scope and point control permissions are required."));
        var active = await simulator.ReadActiveAsync(FacilityScopeId.From(scopeId), token).ConfigureAwait(false);
        if (active.IsFailure || active.Value.Configuration.ScopeId != scope)
            return Problem(active.IsFailure ? active.Error! : new OperationError(ErrorCode.From("command.active_revision_stale"), "Active Simulator scope does not match."));
        if (!registry.TryGet(scope, out var runtime))
            return Problem(new OperationError(ErrorCode.From("runtime.scope_not_found"), "Runtime scope was not found."));
        var current = runtime!.GetSnapshot();
        var entry = current.Entries.SingleOrDefault(value => value.PointId == point);
        if (entry is null)
            return Problem(new OperationError(ErrorCode.From("command.target_unavailable"), "Target current evidence is unavailable."));
        return Results.Ok(new CommandContextPayload(
            scopeId, pointId, active.Value.Receipt.RevisionId.Value, active.Value.Receipt.RevisionNumber.Value,
            active.Value.Generation, active.Value.Receipt.ManifestFingerprint, current.Position.Value,
            entry.Value.Value, entry.Unit.Symbol, entry.Quality.ToString(), entry.Freshness.ToString()));
    }

    private static async Task<IResult> AcquireLeaseAsync(
        AcquireControlLeasePayload request, HttpContext context, RequestSessionResolver sessions,
        IWallClock clock, IdentityStore identities, CommandStore commands,
        CommandSecurityPolicy policy, CancellationToken token)
    {
        var session = sessions.Resolve(context);
        var authorization = SessionAuthorization.AuthorizeMutation(session, CommandPermissions.AcquireLease, clock);
        if (authorization.IsFailure) return Problem(authorization.Error!);
        StepUpAttestation? attestation = null;
        if (policy.StepUpRequired)
        {
            if (string.IsNullOrWhiteSpace(request.StepUpPassword))
                return Problem(new OperationError(ErrorCode.From("command.step_up_required"), "Step-up password is required."));
            var verified = await identities.VerifyStepUpAsync(
                authorization.Value.Session, request.StepUpPassword, policy.StepUpLifetime, token).ConfigureAwait(false);
            if (verified.IsFailure) return Problem(verified.Error!);
            attestation = verified.Value;
        }
        var result = await commands.AcquireLeaseAsync(authorization.Value, new(
            ControlLeaseId.From(request.LeaseId), RuntimeScopeId.From(request.ScopeId),
            TimeSpan.FromSeconds(request.LifetimeSeconds)), attestation, token).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);
    }

    private static async Task<IResult> RevokeLeaseAsync(
        Guid leaseId, RevokeControlLeasePayload request, HttpContext context,
        RequestSessionResolver sessions, IWallClock clock, CommandStore commands, CancellationToken token)
    {
        var authorization = SessionAuthorization.AuthorizeMutation(
            sessions.Resolve(context), CommandPermissions.AcquireLease, clock);
        if (authorization.IsFailure) return Problem(authorization.Error!);
        var result = await commands.RevokeLeaseAsync(
            authorization.Value, ControlLeaseId.From(leaseId), request.Reason, token).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);
    }

    private static async Task<IResult> PrepareAsync(
        PrepareCommandPayload request, HttpContext context, RequestSessionResolver sessions,
        IWallClock clock, CommandStore commands, SimulatorRuntimeStore simulator,
        RuntimeRegistry registry, CancellationToken token)
    {
        var authorization = SessionAuthorization.AuthorizeMutation(
            sessions.Resolve(context), CommandPermissions.Prepare, clock);
        if (authorization.IsFailure) return Problem(authorization.Error!);
        var scope = RuntimeScopeId.From(request.ScopeId);
        var active = await simulator.ReadActiveAsync(FacilityScopeId.From(request.ScopeId), token).ConfigureAwait(false);
        if (active.IsFailure) return Problem(active.Error!);
        if (!registry.TryGet(scope, out var runtime))
            return Problem(new OperationError(ErrorCode.From("runtime.scope_not_found"), "Runtime scope was not found."));
        var result = await commands.PrepareAsync(authorization.Value, new(
            CommandIntentId.From(request.IntentId), ControlLeaseId.From(request.LeaseId), scope,
            PointId.From(request.PointId), request.DesiredValue, Unit.FromSymbol(request.Unit),
            (CommandInteractionMode)request.InteractionMode,
            ConfigurationRevisionId.From(request.ExpectedRevisionId), RevisionNumber.From(request.ExpectedRevisionNumber),
            request.ExpectedGeneration, request.ExpectedManifestFingerprint, request.ExpectedCurrentPosition),
            active.Value, runtime!.GetSnapshot(), token).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);
    }

    private static async Task<IResult> ExecuteAsync(
        ExecuteCommandPayload request, HttpContext context, RequestSessionResolver sessions,
        IWallClock clock, CommandExecutionStore executions, SimulatorRuntimeStore simulator,
        RuntimeRegistry registry, CancellationToken token)
    {
        var authorization = SessionAuthorization.AuthorizeMutation(
            sessions.Resolve(context), CommandPermissions.Execute, clock);
        if (authorization.IsFailure) return Problem(authorization.Error!);
        var scope = RuntimeScopeId.From(request.ScopeId);
        var active = await simulator.ReadActiveAsync(FacilityScopeId.From(request.ScopeId), token).ConfigureAwait(false);
        if (active.IsFailure) return Problem(active.Error!);
        if (!registry.TryGet(scope, out var runtime))
            return Problem(new OperationError(ErrorCode.From("runtime.scope_not_found"), "Runtime scope was not found."));
        var result = await executions.ExecuteAsync(authorization.Value, new(
            CommandExecutionId.From(request.ExecutionId), CommandIntentId.From(request.IntentId),
            scope, PointId.From(request.PointId)), active.Value, runtime!.GetSnapshot(), token).ConfigureAwait(false);
        return ExecutionResponse(result);
    }

    private static async Task<IResult> ReconcileAsync(
        Guid executionId, ReconcileCommandPayload request, HttpContext context,
        RequestSessionResolver sessions, IWallClock clock, CommandExecutionStore executions,
        CancellationToken token)
    {
        var authorization = SessionAuthorization.AuthorizeMutation(
            sessions.Resolve(context), CommandPermissions.Execute, clock);
        if (authorization.IsFailure) return Problem(authorization.Error!);
        var result = await executions.ReconcileAsync(
            authorization.Value, CommandExecutionId.From(executionId), RuntimeScopeId.From(request.ScopeId),
            PointId.From(request.PointId), token).ConfigureAwait(false);
        return ExecutionResponse(result);
    }

    private static IResult ExecutionResponse(Result<CommandExecutionSnapshot> result)
    {
        if (result.IsFailure) return Problem(result.Error!);
        var payload = ToPayload(result.Value);
        return result.Value.State is CommandExecutionState.Accepted or CommandExecutionState.InProgress or CommandExecutionState.Unknown
            ? Results.Accepted(value: payload)
            : Results.Ok(payload);
    }

    internal static CommandExecutionPayload ToPayload(CommandExecutionSnapshot value) => new(
        value.ExecutionId.Value, value.IntentId.Value, value.LeaseId.Value, value.ScopeId.Value,
        value.PointId.Value, value.State.ToString(), value.Progress, value.ResultValue,
        value.RejectionCode, value.AcceptedAt, value.UpdatedAt, value.CompletedAt,
        value.Version.Value, value.Disposition.ToString());

    private static IResult Problem(OperationError error) => Results.Problem(
        statusCode: error.Code.Value switch
        {
            "session.anonymous" or "session.expired" or "session.revoked" or "identity.session_invalid" => StatusCodes.Status401Unauthorized,
            "permission.denied" or "command.step_up_required" or "identity.step_up_invalid" => StatusCodes.Status403Forbidden,
            "runtime.scope_not_found" or "command.lease_not_found" or "command.intent_not_found" or "command.execution_not_found" => StatusCodes.Status404NotFound,
            "command.lease_held" or "command.intent_conflict" or "command.active_revision_stale" or "command.current_stale" or
            "command.execution_identity_conflict" or "command.execution_identity_session" or "command.intent_already_executed" or
            "command.safety_stale" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        }, title: error.Code.Value, detail: error.Message);
}
