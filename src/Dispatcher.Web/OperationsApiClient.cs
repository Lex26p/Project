using System.Net.Http.Json;

namespace Dispatcher.Web;

public sealed record OperationalHealthItemPayload(
    string Owner, Guid? ScopeId, int Facet, int State, string ReasonCode, DateTimeOffset ObservedAt);
public sealed record OperationalHealthViewPayload(
    IReadOnlyList<OperationalHealthItemPayload> Items, int Healthy, int Degraded, int Unavailable, int Unknown);
public sealed record DataQualityIssuePayload(
    Guid IssueId, string Owner, Guid? ScopeId, int Kind, int Severity,
    string Summary, string SourceReference, DateTimeOffset ObservedAt);
public sealed record DataQualityViewPayload(
    IReadOnlyList<DataQualityIssuePayload> Issues, int Warning, int Critical);
public sealed record AuditRecordPayload(
    Guid EventId, string Owner, Guid? ScopeId, Guid? SubjectId, int? PrincipalKind,
    string Action, string TargetKind, Guid? TargetId, int Outcome,
    string Summary, DateTimeOffset OccurredAt);
public sealed record AuditFeedPayload(int Kind, IReadOnlyList<AuditRecordPayload> Records, string? Cursor);

public sealed class OperationsApiClient(HttpClient http)
{
    public Task<OperationalHealthViewPayload?> ReadHealthAsync(CancellationToken token = default) =>
        ReadAsync<OperationalHealthViewPayload>("api/administration/operations/health", token);
    public Task<DataQualityViewPayload?> ReadDataQualityAsync(CancellationToken token = default) =>
        ReadAsync<DataQualityViewPayload>("api/administration/operations/data-quality", token);
    public Task<AuditFeedPayload?> ReadAuditAsync(string? cursor, int pageSize = 50, CancellationToken token = default) =>
        ReadAsync<AuditFeedPayload>(
            cursor is null
                ? $"api/administration/operations/audit?pageSize={pageSize}"
                : $"api/administration/operations/audit?pageSize={pageSize}&cursor={Uri.EscapeDataString(cursor)}",
            token);

    private async Task<T?> ReadAsync<T>(string uri, CancellationToken token)
    {
        var response = await http.GetAsync(uri, token);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>(token) : default;
    }
}
