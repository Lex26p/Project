using Dispatcher.Command;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Facilities;
using Dispatcher.Identity;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Simulator;
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

    private static IResult Problem(OperationError error) => Results.Problem(
        statusCode: error.Code.Value switch
        {
            "session.anonymous" or "session.expired" or "session.revoked" or "identity.session_invalid" => StatusCodes.Status401Unauthorized,
            "permission.denied" or "command.step_up_required" or "identity.step_up_invalid" => StatusCodes.Status403Forbidden,
            "runtime.scope_not_found" or "command.lease_not_found" => StatusCodes.Status404NotFound,
            "command.lease_held" or "command.intent_conflict" or "command.active_revision_stale" or "command.current_stale" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        }, title: error.Code.Value, detail: error.Message);
}
