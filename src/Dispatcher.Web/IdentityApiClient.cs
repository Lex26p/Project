using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Dispatcher.Web;

public sealed record ProductionSessionPayload(
    Guid AccountId, Guid SessionId, string AccessToken, string RefreshToken,
    DateTimeOffset ExpiresAt, DateTimeOffset RefreshExpiresAt);
public sealed record IdentityDiagnosticPayload(
    int Kind, int Status, string Summary, bool SecretConfigured, DateTimeOffset CheckedAt);
public sealed record IdentityGrantPayload(string Permission, Guid? ScopeId);
public sealed record RoleImpactPayload(
    Guid RoleId, IReadOnlyList<string> Added, IReadOnlyList<string> Removed,
    int AffectedAccounts, int ActiveSessions, string Fingerprint);

public sealed class IdentitySessionState
{
    public ProductionSessionPayload? Session { get; private set; }
    public bool IsAuthenticated => Session is not null;
    internal void Set(ProductionSessionPayload value) => Session = value;
    internal void Clear() => Session = null;
}

public sealed class IdentityApiClient(HttpClient http, IdentitySessionState state)
{
    public async Task<bool> LoginAsync(string userName, string password, CancellationToken token = default)
    {
        var response = await http.PostAsJsonAsync("api/auth/login", new { UserName = userName, Password = password }, token);
        if (!response.IsSuccessStatusCode) return false;
        Apply((await response.Content.ReadFromJsonAsync<ProductionSessionPayload>(token))!);
        return true;
    }

    public async Task<bool> RefreshAsync(CancellationToken token = default)
    {
        if (state.Session is null) return false;
        var response = await http.PostAsJsonAsync("api/auth/refresh", new { state.Session.RefreshToken }, token);
        if (!response.IsSuccessStatusCode) { Clear(); return false; }
        Apply((await response.Content.ReadFromJsonAsync<ProductionSessionPayload>(token))!);
        return true;
    }

    public async Task RevokeAsync(CancellationToken token = default)
    {
        if (state.Session is not null) await http.PostAsync("api/auth/revoke", null, token);
        Clear();
    }

    public async Task<IdentityDiagnosticPayload?> ReadDiagnosticAsync(CancellationToken token = default)
    {
        var response = await http.GetAsync("api/administration/identity/diagnostics/local-authentication", token);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<IdentityDiagnosticPayload>(token)
            : null;
    }

    public Task<HttpResponseMessage> CreateScopeAsync(Guid id, string name, Guid? parent, CancellationToken token = default) =>
        http.PostAsJsonAsync("api/administration/identity/scopes", new { ScopeId = id, Name = name, ParentScopeId = parent }, token);
    public Task<HttpResponseMessage> CreateRoleAsync(Guid id, string name, IdentityGrantPayload grant, CancellationToken token = default) =>
        http.PostAsJsonAsync("api/administration/identity/roles", new { RoleId = id, Name = name, Grants = new[] { grant } }, token);
    public Task<HttpResponseMessage> CreateGroupAsync(Guid id, string name, CancellationToken token = default) =>
        http.PostAsJsonAsync("api/administration/identity/groups", new { GroupId = id, Name = name }, token);
    public Task<HttpResponseMessage> CreateAccountAsync(
        Guid id, Guid subjectId, Guid? primaryScopeId, string userName, string password, CancellationToken token = default) =>
        http.PostAsJsonAsync("api/administration/identity/accounts", new
        {
            AccountId = id, SubjectId = subjectId, WorkspaceAccountId = (Guid?)null,
            PrimaryScopeId = primaryScopeId, UserName = userName, Password = password,
        }, token);
    public Task<HttpResponseMessage> AssignGroupRoleAsync(Guid groupId, Guid roleId, CancellationToken token = default) =>
        http.PostAsync($"api/administration/identity/groups/{groupId}/roles/{roleId}", null, token);
    public Task<HttpResponseMessage> AddGroupMemberAsync(Guid groupId, Guid accountId, CancellationToken token = default) =>
        http.PostAsync($"api/administration/identity/groups/{groupId}/accounts/{accountId}", null, token);
    public Task<HttpResponseMessage> SetGlobalSettingAsync(string key, string value, CancellationToken token = default) =>
        http.PutAsJsonAsync($"api/administration/identity/settings/global/{Uri.EscapeDataString(key)}", new { Value = value }, token);
    public Task<HttpResponseMessage> SetScopedSettingAsync(string target, Guid targetId, string key, string value, CancellationToken token = default) =>
        http.PutAsJsonAsync($"api/administration/identity/settings/{target}/{targetId}/{Uri.EscapeDataString(key)}", new { Value = value }, token);
    public Task<HttpResponseMessage> SetAccountPermissionOverrideAsync(
        Guid accountId, string permission, bool allowed, CancellationToken token = default) =>
        http.PutAsJsonAsync($"api/administration/identity/accounts/{accountId}/permission-override", new { Permission = permission, Allowed = allowed }, token);

    public async Task<RoleImpactPayload?> PreviewRoleAsync(
        Guid roleId, IdentityGrantPayload grant, CancellationToken token = default)
    {
        var response = await http.PostAsJsonAsync(
            $"api/administration/identity/roles/{roleId}/impact", new[] { grant }, token);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<RoleImpactPayload>(token)
            : null;
    }

    public Task<HttpResponseMessage> UpdateRoleAsync(
        Guid roleId, ulong expectedVersion, RoleImpactPayload preview, IdentityGrantPayload grant,
        CancellationToken token = default) =>
        http.PutAsJsonAsync($"api/administration/identity/roles/{roleId}/permissions", new
        {
            ExpectedVersion = expectedVersion,
            PreviewFingerprint = preview.Fingerprint,
            Grants = new[] { grant },
        }, token);

    private void Apply(ProductionSessionPayload session)
    {
        state.Set(session);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Dispatcher-Session", session.AccessToken);
    }

    private void Clear()
    {
        state.Clear();
        http.DefaultRequestHeaders.Authorization = null;
    }
}
