using System.Net.Http.Json;

namespace Dispatcher.Web;

public sealed class KioskApiClient(HttpClient httpClient)
{
    public async Task<KioskAssignedRuntimePayload> SynchronizeAsync(CancellationToken token)
    {
        using var response =
            await httpClient.GetAsync(
                "api/terminal/runtime/sync",
                token);
        EnsureAuthorized(response);
        response.EnsureSuccessStatusCode();
        return await response.Content
            .ReadFromJsonAsync<
                KioskAssignedRuntimePayload>(
                token) ??
            throw new InvalidOperationException(
                "Terminal runtime response was empty.");
    }

    public async Task<KioskHeartbeatPayload> HeartbeatAsync(ulong profileVersion, CancellationToken token)
    {
        var response = await httpClient.PostAsJsonAsync(
            "api/terminal/runtime/heartbeat", new KioskHeartbeatRequest(profileVersion), token);
        EnsureAuthorized(response);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<KioskHeartbeatPayload>(token)
            ?? throw new InvalidOperationException("Terminal heartbeat response was empty.");
    }

    private static void EnsureAuthorized(
        HttpResponseMessage response)
    {
        if (response.StatusCode is
            System.Net.HttpStatusCode
                .Unauthorized or
            System.Net.HttpStatusCode
                .Forbidden)
        {
            throw new KioskAuthorizationException();
        }
    }
}
