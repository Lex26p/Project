using System.Net.Http.Json;

namespace Dispatcher.Web;

public sealed class RegistryApiClient
{
    private readonly HttpClient http;

    public RegistryApiClient(HttpClient http)
    {
        this.http = http;
    }

    public Task<IReadOnlyList<RegistryScopePayload>?> ReadScopesAsync(CancellationToken cancellationToken = default) =>
        ReadOptionalAsync<IReadOnlyList<RegistryScopePayload>>("api/registry/scopes", cancellationToken);

    public Task<LocationListPayload?> ReadLocationsAsync(
        Guid scopeId,
        string query,
        CancellationToken cancellationToken = default) =>
        ReadOptionalAsync<LocationListPayload>(
            $"api/registry/locations?scopeId={scopeId:D}&query={Uri.EscapeDataString(query)}",
            cancellationToken);

    public Task<LocationDetailPayload?> ReadLocationAsync(
        Guid scopeId,
        Guid locationId,
        CancellationToken cancellationToken = default) =>
        ReadOptionalAsync<LocationDetailPayload>(
            $"api/registry/locations/{locationId:D}?scopeId={scopeId:D}",
            cancellationToken);

    public Task<LocationPlanContextPayload?> ReadPlanContextAsync(
        Guid scopeId,
        Guid locationId,
        CancellationToken cancellationToken = default) =>
        ReadOptionalAsync<LocationPlanContextPayload>(
            $"api/registry/locations/{locationId:D}/plan-context?scopeId={scopeId:D}",
            cancellationToken);

    public Task<EquipmentListPayload?> ReadEquipmentAsync(
        Guid scopeId,
        string query,
        CancellationToken cancellationToken = default) =>
        ReadOptionalAsync<EquipmentListPayload>(
            $"api/registry/equipment?scopeId={scopeId:D}&query={Uri.EscapeDataString(query)}",
            cancellationToken);

    public Task<EquipmentDetailPayload?> ReadEquipmentAsync(
        Guid scopeId,
        Guid equipmentId,
        CancellationToken cancellationToken = default) =>
        ReadOptionalAsync<EquipmentDetailPayload>(
            $"api/registry/equipment/{equipmentId:D}?scopeId={scopeId:D}",
            cancellationToken);

    private async Task<TValue?> ReadOptionalAsync<TValue>(string uri, CancellationToken cancellationToken)
    {
        var response = await http.GetAsync(uri, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<TValue>(cancellationToken)
            : default;
    }
}
