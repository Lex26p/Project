using Dispatcher.Dashboards;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Terminals;
using Dispatcher.Workspace;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Dispatcher.Server;

public sealed record TerminalAssignedRuntimePayload(
    Guid TerminalId, Guid DeviceIdentityId, Guid ProfileId, ulong ProfileVersion,
    string Experience, string OfflineMode, bool EmployeeReauthenticationRequired,
    DashboardManifestPayload Dashboard, DateTimeOffset SynchronizedAt);
public sealed record TerminalHeartbeatRequest(ulong SynchronizedProfileVersion);
public sealed record TerminalHeartbeatPayload(DateTimeOffset AcceptedAt, ulong ProfileVersion, bool ResyncRequired);
public sealed record TerminalReauthenticationRequest(Guid PersonId, string Pin);
public sealed record TerminalReauthenticationPayload(Guid PersonId, string Token, DateTimeOffset ExpiresAt);
public sealed record TerminalInteractionRequest(Guid InteractionId, string Action, string? ReauthenticationToken);
public sealed record TerminalInteractionPayload(Guid InteractionId, Guid TerminalId, Guid DeviceIdentityId, Guid? PersonId, string Action, DateTimeOffset AcceptedAt);
public sealed record TerminalCommandRequest(bool Online);

public sealed class AuthorizedTerminalRuntimeService
{
    private readonly TerminalStore terminals;
    private readonly AuthorizedDashboardService dashboards;
    private readonly IWallClock clock;

    public AuthorizedTerminalRuntimeService(
        TerminalStore terminals, AuthorizedDashboardService dashboards, IWallClock clock)
    {
        this.terminals = terminals;
        this.dashboards = dashboards;
        this.clock = clock;
    }

    public Task<Result<AuthenticatedTerminal>> AuthenticateAsync(
        TerminalCredentialPresentation? credential, CancellationToken token) =>
        terminals.AuthenticateAsync(credential, token);

    public async Task<Result<TerminalAssignedRuntimePayload>> SynchronizeAsync(
        AuthenticatedTerminal terminal, CancellationToken token)
    {
        var sync = terminals.Synchronize(terminal);
        if (sync.IsFailure) return Result.Failure<TerminalAssignedRuntimePayload>(sync.Error!);
        if (sync.Value.Content.Kind != TerminalContentKind.Dashboard)
            return Failure<TerminalAssignedRuntimePayload>("terminal.content_unsupported", "Assigned content is not a Dashboard runtime.");
        var now = clock.GetUtcNow();
        var permissions = sync.Value.Policy.RuntimePermissions
            .Append(DashboardPermissions.Read(DashboardId.From(sync.Value.Content.ContentId))).Distinct().ToArray();
        var session = new SessionSnapshot(
            SessionId.New(), SubjectId.From(sync.Value.DeviceIdentityId.Value), PrincipalKind.Device,
            now, now.AddMinutes(1), new EffectivePermissions(permissions));
        var manifest = await dashboards.ReadManifestAsync(
            session, DashboardId.From(sync.Value.Content.ContentId), token).ConfigureAwait(false);
        if (manifest.IsFailure) return Result.Failure<TerminalAssignedRuntimePayload>(manifest.Error!);
        return Result.Success(new TerminalAssignedRuntimePayload(
            sync.Value.TerminalId.Value, sync.Value.DeviceIdentityId.Value, sync.Value.ProfileId.Value,
            sync.Value.ProfileVersion.Value, sync.Value.Policy.Experience.ToString(),
            sync.Value.Policy.OfflineMode.ToString(),
            sync.Value.Policy.EmployeeReauthentication == TerminalEmployeeReauthentication.Required,
            DashboardEndpoints.ToPayload(manifest.Value), sync.Value.SynchronizedAt));
    }

    public Task<Result<TerminalHeartbeat>> HeartbeatAsync(
        AuthenticatedTerminal terminal, StateVersion synchronizedVersion, CancellationToken token) =>
        terminals.HeartbeatAsync(terminal, synchronizedVersion, token);

    public Task<Result<TerminalEmployeeReauthenticationIssue>> ReauthenticateAsync(
        AuthenticatedTerminal terminal, PersonId personId, string pin, CancellationToken token) =>
        terminals.ReauthenticateEmployeeAsync(terminal, personId, pin, token);

    public Task<Result<TerminalInteractionAttribution>> AuthorizeInteractionAsync(
        AuthenticatedTerminal terminal, TerminalInteractionRequest request, CancellationToken token) =>
        terminals.AuthorizeInteractionAsync(
            terminal, request.InteractionId, request.Action,
            request.ReauthenticationToken is null ? null : new(request.ReauthenticationToken), token);

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));
}

public static class TerminalRuntimeEndpoints
{
    public static IServiceCollection AddTerminalRuntimeServer(
        this IServiceCollection services, string connectionString, string databaseRole,
        TerminalEnrollmentPolicy enrollmentPolicy, TerminalPinPolicy pinPolicy)
    {
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(sp => new TerminalStore(
            sp.GetRequiredService<NpgsqlDataSource>(), databaseRole,
            sp.GetRequiredService<IWallClock>(), enrollmentPolicy, pinPolicy));
        services.AddSingleton<AuthorizedTerminalRuntimeService>();
        return services;
    }

    public static IEndpointRouteBuilder MapTerminalRuntimeServer(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/terminal/runtime");
        group.MapGet("/sync", SyncAsync);
        group.MapPost("/heartbeat", HeartbeatAsync);
        group.MapPost("/reauthenticate", ReauthenticateAsync);
        group.MapPost("/interactions", InteractionAsync);
        group.MapPost("/commands", CommandAsync);
        return endpoints;
    }

    private static async Task<IResult> SyncAsync(
        HttpContext context, AuthorizedTerminalRuntimeService runtime, CancellationToken token)
    {
        var terminal = await AuthenticateAsync(context, runtime, token).ConfigureAwait(false);
        if (terminal.IsFailure) return Problem(terminal.Error!);
        var result = await runtime.SynchronizeAsync(terminal.Value, token).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);
    }

    private static async Task<IResult> HeartbeatAsync(
        TerminalHeartbeatRequest request, HttpContext context,
        AuthorizedTerminalRuntimeService runtime, CancellationToken token)
    {
        var terminal = await AuthenticateAsync(context, runtime, token).ConfigureAwait(false);
        if (terminal.IsFailure) return Problem(terminal.Error!);
        var result = await runtime.HeartbeatAsync(terminal.Value, StateVersion.From(request.SynchronizedProfileVersion), token)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Ok(new TerminalHeartbeatPayload(result.Value.AcceptedAt, result.Value.ProfileVersion.Value, result.Value.ResyncRequired))
            : Problem(result.Error!);
    }

    private static async Task<IResult> ReauthenticateAsync(
        TerminalReauthenticationRequest request, HttpContext context,
        AuthorizedTerminalRuntimeService runtime, CancellationToken token)
    {
        var terminal = await AuthenticateAsync(context, runtime, token).ConfigureAwait(false);
        if (terminal.IsFailure) return Problem(terminal.Error!);
        var result = await runtime.ReauthenticateAsync(terminal.Value, PersonId.From(request.PersonId), request.Pin, token)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Ok(new TerminalReauthenticationPayload(result.Value.PersonId.Value, result.Value.Token, result.Value.ExpiresAt))
            : Problem(result.Error!);
    }

    private static async Task<IResult> InteractionAsync(
        TerminalInteractionRequest request, HttpContext context,
        AuthorizedTerminalRuntimeService runtime, CancellationToken token)
    {
        var terminal = await AuthenticateAsync(context, runtime, token).ConfigureAwait(false);
        if (terminal.IsFailure) return Problem(terminal.Error!);
        var result = await runtime.AuthorizeInteractionAsync(terminal.Value, request, token).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Ok(new TerminalInteractionPayload(
                result.Value.InteractionId, result.Value.TerminalId.Value, result.Value.DeviceIdentityId.Value,
                result.Value.PersonId?.Value, result.Value.Action, result.Value.AcceptedAt))
            : Problem(result.Error!);
    }

    private static async Task<IResult> CommandAsync(
        TerminalCommandRequest request, HttpContext context,
        AuthorizedTerminalRuntimeService runtime, CancellationToken token)
    {
        var terminal = await AuthenticateAsync(context, runtime, token).ConfigureAwait(false);
        if (terminal.IsFailure) return Problem(terminal.Error!);
        var result = TerminalCommandAdmission.Evaluate(terminal.Value.Profile?.RuntimePolicy ?? TerminalRuntimePolicy.Default, request.Online);
        return Problem(result.Error!);
    }

    private static Task<Result<AuthenticatedTerminal>> AuthenticateAsync(
        HttpContext context, AuthorizedTerminalRuntimeService runtime, CancellationToken token) =>
        runtime.AuthenticateAsync(TerminalRequestIdentity.Resolve(
            context.Request.Headers.Authorization.ToString(),
            context.Request.Query.ToDictionary(value => value.Key, value => (string?)value.Value.ToString())), token);

    private static IResult Problem(OperationError error) => Results.Problem(
        statusCode: error.Code.Value switch
        {
            "terminal.authentication_required" or "terminal.credential_invalid" or "terminal.credential_expired" => StatusCodes.Status401Unauthorized,
            "terminal.not_active" or "terminal.wallboard_command_denied" or "terminal.offline_command_not_queued" or
                "terminal.command_unavailable" or "terminal.reauthentication_required" => StatusCodes.Status403Forbidden,
            "dashboard.not_found" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        }, title: error.Code.Value, detail: error.Message);
}
