using System.Net;
using System.Net.Http.Json;

namespace Dispatcher.Web;

public enum RouteAccess
{
    Allowed = 1,
    Denied = 2,
    SessionExpired = 3,
}

public sealed class WorkspaceApiClient
{
    private readonly HttpClient http;

    public WorkspaceApiClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
    }

    public async Task<RouteAccess> CheckAccessAsync(string route, CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync(
            $"api/workspace/access?route={Uri.EscapeDataString(route)}",
            cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return RouteAccess.Allowed;
        }

        return response.StatusCode == HttpStatusCode.Unauthorized
            ? RouteAccess.SessionExpired
            : RouteAccess.Denied;
    }

    public Task<IReadOnlyList<WorkspaceNavigationPayload>?> ReadNavigationAsync(
        CancellationToken cancellationToken = default) =>
        http.GetFromJsonAsync<IReadOnlyList<WorkspaceNavigationPayload>>(
            "api/workspace/navigation",
            cancellationToken);

    public Task<WorkspaceHomePayload?> ReadHomeAsync(CancellationToken cancellationToken = default) =>
        ReadOptionalAsync<WorkspaceHomePayload>("api/workspace/home", cancellationToken);

    public Task<PersonProfilePayload?> ReadMeAsync(CancellationToken cancellationToken = default) =>
        ReadOptionalAsync<PersonProfilePayload>("api/workspace/me", cancellationToken);

    public Task<PersonProfilePayload?> ReadUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        ReadOptionalAsync<PersonProfilePayload>($"api/workspace/users/{userId}", cancellationToken);

    public Task<IReadOnlyList<WorkspaceSearchPayload>?> SearchAsync(
        string query,
        CancellationToken cancellationToken = default) =>
        ReadOptionalAsync<IReadOnlyList<WorkspaceSearchPayload>>(
            $"api/workspace/search?query={Uri.EscapeDataString(query)}",
            cancellationToken);

    public Task<HttpResponseMessage> UpdateProfileAsync(
        UpdateProfileRequest request,
        CancellationToken cancellationToken = default) =>
        http.PutAsJsonAsync("api/workspace/me/profile", request, cancellationToken);

    public Task<HttpResponseMessage> UpdatePreferencesAsync(
        UpdatePreferencesRequest request,
        CancellationToken cancellationToken = default) =>
        http.PutAsJsonAsync("api/workspace/me/preferences", request, cancellationToken);

    public Task<HttpResponseMessage> UpdateHomeItemAsync(
        Guid itemId,
        UpdateHomeOverrideRequest request,
        CancellationToken cancellationToken = default) =>
        http.PutAsJsonAsync($"api/workspace/home/items/{itemId}", request, cancellationToken);

    public Task<HttpResponseMessage> UpdateFavoriteAsync(
        Guid itemId,
        bool favorite,
        CancellationToken cancellationToken = default) =>
        http.PutAsJsonAsync(
            $"api/workspace/favorites/{itemId}",
            new UpdateFavoriteRequest(favorite),
            cancellationToken);

    public Task<HttpResponseMessage> RecordRecentAsync(
        Guid itemId,
        CancellationToken cancellationToken = default) =>
        http.PostAsync($"api/workspace/recent/{itemId}", null, cancellationToken);

    private async Task<TValue?> ReadOptionalAsync<TValue>(
        string uri,
        CancellationToken cancellationToken)
    {
        var response = await http.GetAsync(uri, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<TValue>(cancellationToken)
            : default;
    }
}
