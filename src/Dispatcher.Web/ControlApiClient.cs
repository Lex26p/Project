using System.Net.Http.Json;

namespace Dispatcher.Web;

public sealed record CommandContextPayload(
    Guid ScopeId, Guid PointId, Guid RevisionId, ulong RevisionNumber, long Generation,
    string ManifestFingerprint, ulong CurrentPosition, long CurrentValue, string Unit,
    string Quality, string Freshness);
public sealed record CommandExecutionPayload(
    Guid ExecutionId, Guid IntentId, Guid LeaseId, Guid ScopeId, Guid PointId,
    string State, byte Progress, long? ResultValue, string? RejectionCode,
    DateTimeOffset AcceptedAt, DateTimeOffset UpdatedAt, DateTimeOffset? CompletedAt,
    ulong Version, string Disposition);
public sealed record CommandExecutionTransitionPayload(
    ulong Position, Guid ExecutionId, Guid PointId, string State, byte Progress,
    long? ResultValue, string? RejectionCode, DateTimeOffset OccurredAt, ulong Version);
public sealed record CommandExecutionSnapshotPayload(
    ulong Cursor, IReadOnlyList<CommandExecutionPayload> Executions);
public sealed record CommandExecutionFeedPayload(
    string Kind, ulong From, ulong To, IReadOnlyList<CommandExecutionTransitionPayload> Transitions);

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

    public Task<HttpResponseMessage> ExecuteAsync(
        Guid executionId, Guid intentId, Guid scopeId, Guid pointId,
        CancellationToken token = default) =>
        http.PostAsJsonAsync("api/commands/executions", new
        {
            ExecutionId = executionId, IntentId = intentId, ScopeId = scopeId, PointId = pointId,
        }, token);

    public Task<HttpResponseMessage> ReconcileAsync(
        Guid executionId, Guid scopeId, Guid pointId, CancellationToken token = default) =>
        http.PostAsJsonAsync($"api/commands/executions/{executionId}/reconcile", new
        {
            ScopeId = scopeId, PointId = pointId,
        }, token);

    public static Task<CommandExecutionPayload?> ReadExecutionAsync(
        HttpResponseMessage response, CancellationToken token = default) =>
        response.IsSuccessStatusCode
            ? response.Content.ReadFromJsonAsync<CommandExecutionPayload>(token)
            : Task.FromResult<CommandExecutionPayload?>(null);
}
