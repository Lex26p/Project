using System.Net.Http.Json;

namespace Dispatcher.Web;

public sealed record CommandContextPayload(
    Guid ScopeId, Guid PointId, Guid RevisionId, ulong RevisionNumber, long Generation,
    string ManifestFingerprint, ulong CurrentPosition, long CurrentValue, string Unit,
    string Quality, string Freshness);

public sealed class ControlApiClient(HttpClient http)
{
    public async Task<CommandContextPayload?> ReadContextAsync(
        Guid scopeId, Guid pointId, CancellationToken token = default)
    {
        var response = await http.GetAsync($"api/commands/context/{scopeId}/{pointId}", token);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<CommandContextPayload>(token)
            : null;
    }

    public Task<HttpResponseMessage> AcquireLeaseAsync(
        Guid leaseId, Guid scopeId, int lifetimeSeconds, string? stepUpPassword,
        CancellationToken token = default) =>
        http.PostAsJsonAsync("api/commands/leases", new
        {
            LeaseId = leaseId, ScopeId = scopeId, LifetimeSeconds = lifetimeSeconds,
            StepUpPassword = stepUpPassword,
        }, token);

    public Task<HttpResponseMessage> PrepareAsync(
        Guid intentId, Guid leaseId, CommandContextPayload context, long desiredValue,
        CancellationToken token = default) =>
        http.PostAsJsonAsync("api/commands/intents/prepare", new
        {
            IntentId = intentId, LeaseId = leaseId, context.ScopeId, context.PointId,
            DesiredValue = desiredValue, context.Unit, InteractionMode = 1,
            ExpectedRevisionId = context.RevisionId,
            ExpectedRevisionNumber = context.RevisionNumber,
            ExpectedGeneration = context.Generation,
            ExpectedManifestFingerprint = context.ManifestFingerprint,
            ExpectedCurrentPosition = context.CurrentPosition,
        }, token);

    public Task<HttpResponseMessage> RevokeLeaseAsync(
        Guid leaseId, CancellationToken token = default) =>
        http.PostAsJsonAsync($"api/commands/leases/{leaseId}/revoke", new { Reason = "operator-release" }, token);
}
