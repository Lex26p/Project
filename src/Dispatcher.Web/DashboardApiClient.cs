using System.Net.Http.Json;

namespace Dispatcher.Web;

public sealed class DashboardApiClient(HttpClient httpClient)
{
    public async Task<IReadOnlyList<
        DashboardCatalogPayload>> ReadCatalogAsync(
            CancellationToken cancellationToken)
    {
        var catalog =
            await httpClient.GetFromJsonAsync<
                    DashboardCatalogPayload[]>(
                    "api/dashboards",
                    cancellationToken)
                .ConfigureAwait(false);
        return catalog ?? [];
    }

    public async Task<DashboardManifestPayload>
        ReadManifestAsync(
            Guid dashboardId,
            Guid? windowId,
            CancellationToken cancellationToken)
    {
        var uri =
            $"api/dashboards/{dashboardId:D}";
        if (windowId is not null)
        {
            uri +=
                $"?windowId={windowId.Value:D}";
        }

        return await httpClient.GetFromJsonAsync<
                DashboardManifestPayload>(
                uri,
                cancellationToken)
            .ConfigureAwait(false) ??
            throw new InvalidOperationException(
                "Dashboard runtime manifest response was empty.");
    }

    public async Task RecordOpenedAsync(
        Guid dashboardId,
        CancellationToken cancellationToken)
    {
        using var response =
            await httpClient.PostAsync(
                    $"api/dashboards/{dashboardId:D}/opened",
                    content: null,
                    cancellationToken)
                .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

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
