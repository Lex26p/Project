using System.Net;
using System.Net.Http.Json;

namespace Dispatcher.Web;

public sealed class IncidentMyWorkApiClient(HttpClient http)
{
    public Task<IncidentMyWorkApiResult<CreateIncidentWorkflowPayload>> CreateFromOccurrenceAsync(
        Guid scopeId,
        Guid occurrenceId,
        string summary,
        bool createTask,
        DateTimeOffset? dueAt,
        CancellationToken cancellationToken = default)
    {
        var incidentId = Guid.NewGuid();
        return SendAsync<CreateIncidentFromEventInput, CreateIncidentWorkflowPayload>(
            HttpMethod.Post,
            $"api/events/{scopeId:D}/occurrences/{occurrenceId:D}/incident",
            new CreateIncidentFromEventInput(
                incidentId,
                summary,
                null,
                createTask,
                createTask ? Guid.NewGuid() : null,
                createTask ? summary : null,
                null,
                dueAt,
                Guid.NewGuid().ToString("N")),
            cancellationToken);
    }

    public Task<IncidentMyWorkApiResult<IncidentSummaryPayload>> ReadIncidentAsync(
        Guid incidentId,
        CancellationToken cancellationToken = default) =>
        ReadAsync<IncidentSummaryPayload>($"api/incidents/{incidentId:D}", cancellationToken);

    public Task<IncidentMyWorkApiResult<IReadOnlyList<MyWorkItemPayload>>> ReadMyWorkAsync(
        CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<MyWorkItemPayload>>("api/my-work/", cancellationToken);

    public Task<IncidentMyWorkApiResult<MyWorkCountersPayload>> ReadCountersAsync(
        CancellationToken cancellationToken = default) =>
        ReadAsync<MyWorkCountersPayload>("api/my-work/counters", cancellationToken);

    public Task<IncidentMyWorkApiResult<MyWorkItemPayload>> ReadTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        ReadAsync<MyWorkItemPayload>($"api/my-work/tasks/{taskId:D}", cancellationToken);

    public Task<IncidentMyWorkApiResult<IReadOnlyList<TransferCandidatePayload>>> FindCandidatesAsync(
        string query,
        CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<TransferCandidatePayload>>(
            $"api/my-work/transfer-candidates?query={Uri.EscapeDataString(query)}",
            cancellationToken);

    public Task<IncidentMyWorkApiResult<IncidentTaskPayload>> AcceptAsync(
        MyWorkItemPayload task,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(task, "accept", null, null, cancellationToken);

    public Task<IncidentMyWorkApiResult<IncidentTaskPayload>> TransferAsync(
        MyWorkItemPayload task,
        Guid personId,
        string reason,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(task, "transfer", personId, reason, cancellationToken);

    public Task<IncidentMyWorkApiResult<IncidentTaskPayload>> ReturnAsync(
        MyWorkItemPayload task,
        string reason,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(task, "return", null, reason, cancellationToken);

    private Task<IncidentMyWorkApiResult<IncidentTaskPayload>> TransitionAsync(
        MyWorkItemPayload task,
        string action,
        Guid? transferTo,
        string? reason,
        CancellationToken cancellationToken) =>
        SendAsync<TransitionMyWorkTaskInput, IncidentTaskPayload>(
            HttpMethod.Post,
            $"api/my-work/tasks/{task.SourceItemId:D}/{action}",
            new TransitionMyWorkTaskInput(
                task.SourceVersion,
                Guid.NewGuid().ToString("N"),
                transferTo,
                reason),
            cancellationToken);

    private async Task<IncidentMyWorkApiResult<TPayload>> ReadAsync<TPayload>(
        string uri,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(uri, cancellationToken);
        return await ResultAsync<TPayload>(response, cancellationToken);
    }

    private async Task<IncidentMyWorkApiResult<TResponse>> SendAsync<TRequest, TResponse>(
        HttpMethod method,
        string uri,
        TRequest request,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, uri)
        {
            Content = JsonContent.Create(request),
        };
        using var response = await http.SendAsync(message, cancellationToken);
        return await ResultAsync<TResponse>(response, cancellationToken);
    }

    private static async Task<IncidentMyWorkApiResult<TPayload>> ResultAsync<TPayload>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return new IncidentMyWorkApiResult<TPayload>(
                IncidentMyWorkApiStatus.Available,
                await response.Content.ReadFromJsonAsync<TPayload>(cancellationToken));
        }

        return new IncidentMyWorkApiResult<TPayload>(
            response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => IncidentMyWorkApiStatus.SessionExpired,
                HttpStatusCode.Forbidden => IncidentMyWorkApiStatus.Forbidden,
                HttpStatusCode.BadRequest => IncidentMyWorkApiStatus.Invalid,
                HttpStatusCode.NotFound => IncidentMyWorkApiStatus.NotFound,
                HttpStatusCode.Conflict => IncidentMyWorkApiStatus.Conflict,
                _ => IncidentMyWorkApiStatus.Unavailable,
            },
            default);
    }
}
