using System.Net.Http.Json;

namespace Dispatcher.Web;

public sealed class KioskApiClient(HttpClient httpClient)
{
    public async Task<KioskAssignedRuntimePayload> SynchronizeAsync(CancellationToken token) =>
        await httpClient.GetFromJsonAsync<KioskAssignedRuntimePayload>("api/terminal/runtime/sync", token)
        ?? throw new InvalidOperationException("Terminal runtime response was empty.");

    public async Task<KioskHeartbeatPayload> HeartbeatAsync(ulong profileVersion, CancellationToken token)
    {
        var response = await httpClient.PostAsJsonAsync(
            "api/terminal/runtime/heartbeat", new KioskHeartbeatRequest(profileVersion), token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<KioskHeartbeatPayload>(token)
            ?? throw new InvalidOperationException("Terminal heartbeat response was empty.");
    }
}
