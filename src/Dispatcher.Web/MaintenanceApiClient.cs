using System.Net;
using System.Net.Http.Json;

namespace Dispatcher.Web;

public sealed class MaintenanceApiClient(HttpClient http)
{
    public Task<MaintenanceApiResult<MaintenanceOverviewPayload>> ReadOverviewAsync(
        Guid scopeId, CancellationToken token = default) =>
        ReadAsync<MaintenanceOverviewPayload>($"api/maintenance/overview?scopeId={scopeId:D}", token);

    public Task<MaintenanceApiResult<MaintenanceAssetPagePayload>> ReadAssetsAsync(
        Guid scopeId, CancellationToken token = default) =>
        ReadAsync<MaintenanceAssetPagePayload>(
            $"api/maintenance/assets?scopeId={scopeId:D}&pageSize=50", token);

    public Task<MaintenanceApiResult<MaintenanceForecastPagePayload>> ReadCalendarAsync(
        Guid scopeId, DateOnly from, DateOnly to, CancellationToken token = default) =>
        ReadAsync<MaintenanceForecastPagePayload>(
            $"api/maintenance/calendar?scopeId={scopeId:D}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&pageSize=50", token);

    public Task<MaintenanceApiResult<MaintenanceForecastPagePayload>> ReadForecastAsync(
        Guid scopeId, DateOnly from, DateOnly to, CancellationToken token = default) =>
        ReadAsync<MaintenanceForecastPagePayload>(
            $"api/maintenance/forecast?scopeId={scopeId:D}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&pageSize=50", token);

    public Task<MaintenanceApiResult<MaintenanceRequestPagePayload>> ReadRequestsAsync(
        Guid scopeId, CancellationToken token = default) =>
        ReadAsync<MaintenanceRequestPagePayload>(
            $"api/maintenance/requests?scopeId={scopeId:D}&pageSize=50", token);

    public Task<MaintenanceApiResult<MaintenanceDefectPagePayload>> ReadDefectsAsync(
        Guid scopeId, CancellationToken token = default) =>
        ReadAsync<MaintenanceDefectPagePayload>(
            $"api/maintenance/defects?scopeId={scopeId:D}&pageSize=50", token);

    public Task<MaintenanceApiResult<MaintenanceWorkOrderPagePayload>> ReadWorkOrdersAsync(
        Guid scopeId, CancellationToken token = default) =>
        ReadAsync<MaintenanceWorkOrderPagePayload>(
            $"api/maintenance/work-orders?scopeId={scopeId:D}&pageSize=50", token);

    public Task<MaintenanceApiResult<MaintenanceWorkOrderPayload>> ReadWorkOrderAsync(
        Guid workOrderId, CancellationToken token = default) =>
        ReadAsync<MaintenanceWorkOrderPayload>(
            $"api/maintenance/work-orders/{workOrderId:D}", token);

    public Task<MaintenanceApiResult<MaintenanceWorkOrderPayload>> ClaimAsync(
        MaintenanceWorkOrderPayload value, CancellationToken token = default) =>
        TransitionAsync(value, "claim", false, token);

    public Task<MaintenanceApiResult<MaintenanceWorkOrderPayload>> AcceptAsync(
        MaintenanceWorkOrderPayload value, CancellationToken token = default) =>
        TransitionAsync(value, "accept", true, token);

    public Task<MaintenanceApiResult<MaintenanceWorkOrderPayload>> StartAsync(
        MaintenanceWorkOrderPayload value, CancellationToken token = default) =>
        TransitionAsync(value, "start", false, token);

    public Task<MaintenanceApiResult<MaintenanceWorkOrderPayload>> SubmitAsync(
        MaintenanceWorkOrderPayload value, CancellationToken token = default) =>
        TransitionAsync(value, "submit-for-acceptance", false, token);

    public Task<MaintenanceApiResult<MaintenanceWorkOrderPayload>> AcceptResultAsync(
        MaintenanceWorkOrderPayload value, CancellationToken token = default) =>
        TransitionAsync(value, "accept-result", false, token);

    public Task<MaintenanceApiResult<MaintenanceWorkOrderPayload>> UpdateChecklistAsync(
        MaintenanceWorkOrderPayload value,
        MaintenanceChecklistItemPayload item,
        bool completed,
        CancellationToken token = default) =>
        SendAsync(
            HttpMethod.Put,
            $"api/maintenance/work-orders/{value.WorkOrderId:D}/checklist/{item.ItemId:D}",
            new UpdateMaintenanceChecklistInput(
                completed, value.Version, Guid.NewGuid().ToString("N")),
            token);

    private Task<MaintenanceApiResult<MaintenanceWorkOrderPayload>> TransitionAsync(
        MaintenanceWorkOrderPayload value,
        string action,
        bool safetyAcknowledged,
        CancellationToken token) =>
        SendAsync(
            HttpMethod.Post,
            $"api/maintenance/work-orders/{value.WorkOrderId:D}/{action}",
            new VersionedMaintenanceWorkInput(
                value.Version, Guid.NewGuid().ToString("N"), safetyAcknowledged),
            token);

    private async Task<MaintenanceApiResult<T>> ReadAsync<T>(string uri, CancellationToken token)
    {
        using var response = await http.GetAsync(uri, token);
        return response.IsSuccessStatusCode
            ? new(MaintenanceApiStatus.Available, await response.Content.ReadFromJsonAsync<T>(token))
            : new(ToStatus(response.StatusCode), default);
    }

    private async Task<MaintenanceApiResult<MaintenanceWorkOrderPayload>> SendAsync<T>(
        HttpMethod method, string uri, T input, CancellationToken token)
    {
        using var message = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(input) };
        using var response = await http.SendAsync(message, token);
        if (response.IsSuccessStatusCode)
        {
            var command = await response.Content.ReadFromJsonAsync<MaintenanceWorkOrderCommandPayload>(token);
            return new(MaintenanceApiStatus.Available, command?.WorkOrder);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = await response.Content.ReadFromJsonAsync<MaintenanceWorkOrderConflictPayload>(token);
            return new(MaintenanceApiStatus.Conflict, conflict?.Current);
        }

        return new(ToStatus(response.StatusCode), default);
    }

    private static MaintenanceApiStatus ToStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => MaintenanceApiStatus.SessionExpired,
        HttpStatusCode.Forbidden => MaintenanceApiStatus.Forbidden,
        HttpStatusCode.BadRequest => MaintenanceApiStatus.Invalid,
        HttpStatusCode.NotFound => MaintenanceApiStatus.NotFound,
        HttpStatusCode.Conflict => MaintenanceApiStatus.Conflict,
        _ => MaintenanceApiStatus.Unavailable,
    };

    private sealed record MaintenanceWorkOrderConflictPayload(
        string Code,
        string Message,
        MaintenanceWorkOrderPayload Current);
}
