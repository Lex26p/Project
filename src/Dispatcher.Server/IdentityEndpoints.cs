using Dispatcher.Identity;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Dispatcher.Server;

public sealed class ProductionSessionMiddleware(RequestDelegate next)
{
    public const string SessionItemKey = "Dispatcher.ProductionSession";

    public async Task InvokeAsync(HttpContext context, IdentityStore identities)
    {
        var presentation = Parse(context.Request.Headers.Authorization.ToString());
        if (presentation is not null)
        {
            var session = await identities.ResolveAccessAsync(presentation, context.RequestAborted).ConfigureAwait(false);
            if (session.IsSuccess) context.Items[SessionItemKey] = session.Value;
        }
        await next(context).ConfigureAwait(false);
    }

    internal static ProductionAccessPresentation? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var separator = header.IndexOf(' ');
        return separator > 0 && separator < header.Length - 1
            ? new ProductionAccessPresentation(header[..separator], header[(separator + 1)..].Trim())
            : null;
    }
}

public sealed record LocalLoginPayload(string UserName, string Password);
public sealed record RefreshSessionPayload(string RefreshToken);
public sealed record ProductionSessionPayload(
    Guid AccountId, Guid SessionId, string AccessToken, string RefreshToken,
    DateTimeOffset ExpiresAt, DateTimeOffset RefreshExpiresAt);
public sealed record IdentityGrantPayload(string Permission, Guid? ScopeId);
public sealed record CreateRolePayload(Guid RoleId, string Name, IReadOnlyList<IdentityGrantPayload> Grants);
public sealed record UpdateRolePayload(ulong ExpectedVersion, string PreviewFingerprint, IReadOnlyList<IdentityGrantPayload> Grants);
public sealed record RoleImpactPayload(
    Guid RoleId, IReadOnlyList<string> Added, IReadOnlyList<string> Removed,
    int AffectedAccounts, int ActiveSessions, string Fingerprint);
public sealed record CreateAccountPayload(
    Guid AccountId, Guid SubjectId, Guid? WorkspaceAccountId, Guid? PrimaryScopeId,
    string UserName, string Password);
public sealed record CreateGroupPayload(Guid GroupId, string Name);
public sealed record CreateScopePayload(Guid ScopeId, string Name, Guid? ParentScopeId);
public sealed record IdentitySettingPayload(string Value);
public sealed record IdentityPermissionOverridePayload(string Permission, bool Allowed);

public static class IdentityEndpoints
{
    public static IServiceCollection AddIdentityServer(
        this IServiceCollection services, string connectionString, string databaseRole, IdentitySecurityPolicy policy)
    {
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(sp => new IdentityStore(
            sp.GetRequiredService<NpgsqlDataSource>(), databaseRole,
            sp.GetRequiredService<IWallClock>(), policy));
        return services;
    }

    public static IApplicationBuilder UseProductionSessionAuthentication(this IApplicationBuilder app) =>
        app.UseMiddleware<ProductionSessionMiddleware>();

    public static IEndpointRouteBuilder MapIdentityServer(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/auth");
        auth.MapPost("/login", LoginAsync);
        auth.MapPost("/refresh", RefreshAsync);
        auth.MapPost("/revoke", RevokeAsync);
        var admin = endpoints.MapGroup("/api/administration/identity");
        admin.MapPost("/scopes", CreateScopeAsync);
        admin.MapPost("/roles", CreateRoleAsync);
        admin.MapPost("/groups", CreateGroupAsync);
        admin.MapPost("/accounts", CreateAccountAsync);
        admin.MapPost("/accounts/{accountId:guid}/roles/{roleId:guid}", AssignAccountRoleAsync);
        admin.MapPost("/groups/{groupId:guid}/roles/{roleId:guid}", AssignGroupRoleAsync);
        admin.MapPost("/groups/{groupId:guid}/accounts/{accountId:guid}", AddGroupMemberAsync);
        admin.MapPost("/roles/{roleId:guid}/impact", PreviewRoleAsync);
        admin.MapPut("/roles/{roleId:guid}/permissions", UpdateRoleAsync);
        admin.MapPut("/accounts/{accountId:guid}/permission-override", SetAccountPermissionOverrideAsync);
        admin.MapPut("/settings/global/{key}", SetGlobalSettingAsync);
        admin.MapPut("/settings/scopes/{scopeId:guid}/{key}", SetScopeSettingAsync);
        admin.MapPut("/settings/groups/{groupId:guid}/{key}", SetGroupSettingAsync);
        admin.MapPut("/settings/accounts/{accountId:guid}/{key}", SetAccountSettingAsync);
        admin.MapGet("/diagnostics/local-authentication", ReadDiagnosticAsync);
        return endpoints;
    }

    private static async Task<IResult> LoginAsync(LocalLoginPayload request, IdentityStore store, CancellationToken token) =>
        SessionResult(await store.LoginAsync(new(request.UserName, request.Password), token).ConfigureAwait(false));
    private static async Task<IResult> RefreshAsync(RefreshSessionPayload request, IdentityStore store, CancellationToken token) =>
        SessionResult(await store.RefreshAsync(new(request.RefreshToken), token).ConfigureAwait(false));
    private static async Task<IResult> RevokeAsync(HttpContext context, IdentityStore store, CancellationToken token)
    {
        var presentation = ProductionSessionMiddleware.Parse(context.Request.Headers.Authorization.ToString());
        var result = presentation is null
            ? Result.Failure(new OperationError(ErrorCode.From("identity.session_invalid"), "Production session credential is invalid or expired."))
            : await store.RevokeAsync(presentation.Token, token).ConfigureAwait(false);
        return result.IsSuccess ? Results.NoContent() : Problem(result.Error!);
    }

    private static async Task<IResult> CreateScopeAsync(CreateScopePayload request, HttpContext context,
        RequestSessionResolver sessions, IdentityStore store, IWallClock clock, CancellationToken token)
    {
        var authorization = Admin(sessions.Resolve(context), clock);
        if (authorization.IsFailure) return Problem(authorization.Error!);
        var result = await store.CreateScopeAsync(authorization.Value, new(
            IdentityScopeId.From(request.ScopeId), request.Name,
            request.ParentScopeId is null ? null : IdentityScopeId.From(request.ParentScopeId.Value)), token).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);
    }

    private static async Task<IResult> CreateRoleAsync(CreateRolePayload request, HttpContext context,
        RequestSessionResolver sessions, IdentityStore store, IWallClock clock, CancellationToken token)
    {
        var authorization = Admin(sessions.Resolve(context), clock); if (authorization.IsFailure) return Problem(authorization.Error!);
        var result = await store.CreateRoleAsync(authorization.Value, new(
            IdentityRoleId.From(request.RoleId), request.Name, Grants(request.Grants)), token).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);
    }

    private static async Task<IResult> CreateGroupAsync(CreateGroupPayload request, HttpContext context,
        RequestSessionResolver sessions, IdentityStore store, IWallClock clock, CancellationToken token)
    {
        var authorization = Admin(sessions.Resolve(context), clock); if (authorization.IsFailure) return Problem(authorization.Error!);
        var result = await store.CreateGroupAsync(authorization.Value, new(IdentityGroupId.From(request.GroupId), request.Name), token).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);
    }

    private static async Task<IResult> CreateAccountAsync(CreateAccountPayload request, HttpContext context,
        RequestSessionResolver sessions, IdentityStore store, IWallClock clock, CancellationToken token)
    {
        var authorization = Admin(sessions.Resolve(context), clock); if (authorization.IsFailure) return Problem(authorization.Error!);
        var result = await store.CreateAccountAsync(authorization.Value, new(
            IdentityAccountId.From(request.AccountId), SubjectId.From(request.SubjectId),
            request.WorkspaceAccountId is null ? null : AccountId.From(request.WorkspaceAccountId.Value),
            request.PrimaryScopeId is null ? null : IdentityScopeId.From(request.PrimaryScopeId.Value),
            request.UserName, request.Password), token).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);
    }

    private static async Task<IResult> AssignAccountRoleAsync(Guid accountId, Guid roleId, HttpContext context,
        RequestSessionResolver sessions, IdentityStore store, IWallClock clock, CancellationToken token) =>
        MutationResult(await WithAdminAsync(context, sessions, clock, auth => store.AssignRoleToAccountAsync(
            auth, IdentityAccountId.From(accountId), IdentityRoleId.From(roleId), token)).ConfigureAwait(false));
    private static async Task<IResult> AssignGroupRoleAsync(Guid groupId, Guid roleId, HttpContext context,
        RequestSessionResolver sessions, IdentityStore store, IWallClock clock, CancellationToken token) =>
        MutationResult(await WithAdminAsync(context, sessions, clock, auth => store.AssignRoleToGroupAsync(
            auth, IdentityGroupId.From(groupId), IdentityRoleId.From(roleId), token)).ConfigureAwait(false));
    private static async Task<IResult> AddGroupMemberAsync(Guid groupId, Guid accountId, HttpContext context,
        RequestSessionResolver sessions, IdentityStore store, IWallClock clock, CancellationToken token) =>
        MutationResult(await WithAdminAsync(context, sessions, clock, auth => store.AddGroupMemberAsync(
            auth, IdentityGroupId.From(groupId), IdentityAccountId.From(accountId), token)).ConfigureAwait(false));

    private static async Task<IResult> PreviewRoleAsync(Guid roleId, IReadOnlyList<IdentityGrantPayload> request,
        HttpContext context, RequestSessionResolver sessions, IdentityStore store, IWallClock clock, CancellationToken token)
    {
        var access = SessionAuthorization.AuthorizeAccess(sessions.Resolve(context), IdentityPermissions.Administer, clock);
        if (access.IsFailure) return Problem(access.Error!);
        var result = await store.PreviewRolePermissionsAsync(access.Value, IdentityRoleId.From(roleId), Grants(request), token).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(new RoleImpactPayload(result.Value.RoleId.Value,
            result.Value.Added.Select(value => value.Value).ToArray(), result.Value.Removed.Select(value => value.Value).ToArray(),
            result.Value.AffectedAccounts, result.Value.ActiveSessions, result.Value.Fingerprint)) : Problem(result.Error!);
    }

    private static async Task<IResult> UpdateRoleAsync(Guid roleId, UpdateRolePayload request, HttpContext context,
        RequestSessionResolver sessions, IdentityStore store, IWallClock clock, CancellationToken token)
    {
        var authorization = Admin(sessions.Resolve(context), clock); if (authorization.IsFailure) return Problem(authorization.Error!);
        var result = await store.UpdateRolePermissionsAsync(authorization.Value, IdentityRoleId.From(roleId),
            Grants(request.Grants), StateVersion.From(request.ExpectedVersion), request.PreviewFingerprint, token).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);
    }

    private static async Task<IResult> SetGlobalSettingAsync(string key, IdentitySettingPayload request, HttpContext context,
        RequestSessionResolver sessions, IdentityStore store, IWallClock clock, CancellationToken token) =>
        MutationResult(await WithAdminAsync(context, sessions, clock,
            auth => store.SetGlobalSettingAsync(auth, key, request.Value, token)).ConfigureAwait(false));

    private static async Task<IResult> SetScopeSettingAsync(Guid scopeId, string key, IdentitySettingPayload request,
        HttpContext context, RequestSessionResolver sessions, IdentityStore store, IWallClock clock, CancellationToken token) =>
        MutationResult(await WithAdminAsync(context, sessions, clock, auth => store.SetScopeSettingAsync(
            auth, IdentityScopeId.From(scopeId), key, request.Value, token)).ConfigureAwait(false));
    private static async Task<IResult> SetGroupSettingAsync(Guid groupId, string key, IdentitySettingPayload request,
        HttpContext context, RequestSessionResolver sessions, IdentityStore store, IWallClock clock, CancellationToken token) =>
        MutationResult(await WithAdminAsync(context, sessions, clock, auth => store.SetGroupSettingAsync(
            auth, IdentityGroupId.From(groupId), key, request.Value, token)).ConfigureAwait(false));
    private static async Task<IResult> SetAccountSettingAsync(Guid accountId, string key, IdentitySettingPayload request,
        HttpContext context, RequestSessionResolver sessions, IdentityStore store, IWallClock clock, CancellationToken token) =>
        MutationResult(await WithAdminAsync(context, sessions, clock, auth => store.SetAccountSettingAsync(
            auth, IdentityAccountId.From(accountId), key, request.Value, token)).ConfigureAwait(false));
    private static async Task<IResult> SetAccountPermissionOverrideAsync(Guid accountId, IdentityPermissionOverridePayload request,
        HttpContext context, RequestSessionResolver sessions, IdentityStore store, IWallClock clock, CancellationToken token) =>
        MutationResult(await WithAdminAsync(context, sessions, clock, auth => store.SetAccountPermissionOverrideAsync(
            auth, IdentityAccountId.From(accountId), PermissionCode.From(request.Permission), request.Allowed, token)).ConfigureAwait(false));

    private static async Task<IResult> ReadDiagnosticAsync(HttpContext context, RequestSessionResolver sessions,
        IdentityStore store, IWallClock clock, CancellationToken token)
    {
        var access = SessionAuthorization.AuthorizeAccess(sessions.Resolve(context), IdentityPermissions.ReadDiagnostics, clock);
        if (access.IsFailure) return Problem(access.Error!);
        var result = await store.ReadIntegrationDiagnosticAsync(access.Value, token).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);
    }

    private static Result<AuthorizedMutation> Admin(SessionSnapshot? session, IWallClock clock) =>
        SessionAuthorization.AuthorizeMutation(session, IdentityPermissions.Administer, clock);
    private static async Task<Result> WithAdminAsync(HttpContext context, RequestSessionResolver sessions, IWallClock clock,
        Func<AuthorizedMutation, Task<Result>> action)
    {
        var authorization = Admin(sessions.Resolve(context), clock);
        return authorization.IsFailure ? Result.Failure(authorization.Error!) : await action(authorization.Value).ConfigureAwait(false);
    }
    private static IdentityPermissionGrant[] Grants(IEnumerable<IdentityGrantPayload> grants) => grants.Select(value => new IdentityPermissionGrant(
        PermissionCode.From(value.Permission), value.ScopeId is null ? null : IdentityScopeId.From(value.ScopeId.Value))).ToArray();
    private static IResult SessionResult(Result<ProductionSessionIssue> result) => result.IsSuccess
        ? Results.Ok(new ProductionSessionPayload(result.Value.AccountId.Value, result.Value.Session.Id.Value,
            result.Value.AccessToken, result.Value.RefreshToken, result.Value.Session.ExpiresAt, result.Value.RefreshExpiresAt))
        : Problem(result.Error!);
    private static IResult MutationResult(Result result) => result.IsSuccess ? Results.NoContent() : Problem(result.Error!);
    private static IResult Problem(OperationError error) => Results.Problem(
        statusCode: error.Code.Value switch
        {
            "identity.login_invalid" or "identity.session_invalid" or "session.anonymous" or "session.expired" or "session.revoked" => StatusCodes.Status401Unauthorized,
            "permission.denied" or "identity.last_administrator" => StatusCodes.Status403Forbidden,
            "identity.account_not_found" or "identity.role_not_found" => StatusCodes.Status404NotFound,
            "identity.version_conflict" or "identity.impact_preview_stale" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        }, title: error.Code.Value, detail: error.Message);
}
