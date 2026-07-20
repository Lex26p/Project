using System.Net.Http.Json;

namespace Dispatcher.Web;

public sealed class DashboardApiClient(HttpClient httpClient)
{
    public async Task<DashboardSubscriptionPayload> CreateSubscriptionAsync(
        Guid dashboardId,
        IReadOnlyCollection<Guid> visibleWindowIds,
        CancellationToken cancellationToken)
    {
        var response = await httpClient.PostAsJsonAsync(
            $"api/dashboards/{dashboardId:D}/subscriptions",
            new DashboardSubscriptionRequest(visibleWindowIds.ToArray()),
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DashboardSubscriptionPayload>(cancellationToken)
            ?? throw new InvalidOperationException("Dashboard subscription response was empty.");
    }

    public async Task<bool> IsSubscriptionCurrentAsync(Guid subscriptionId, CancellationToken cancellationToken) =>
        (await httpClient.GetFromJsonAsync<DashboardSubscriptionStatusPayload>(
            $"api/dashboards/subscriptions/{subscriptionId:D}/status",
            cancellationToken))?.IsCurrent ?? false;
}
