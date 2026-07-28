using System.Globalization;
using System.Net;
using System.Net.Http.Json;

namespace Dispatcher.Web;

public sealed class EventApiClient(HttpClient http)
{
    public Task<EventApiResult<
        OccurrenceSnapshotPayload>>
        ReadOccurrenceSnapshotAsync(
            Guid scopeId,
            CancellationToken cancellationToken =
                default) =>
        ReadAsync<OccurrenceSnapshotPayload>(
            $"api/events/{scopeId:D}/occurrences/snapshot",
            cancellationToken);

    public Task<EventApiResult<
        OccurrenceFeedPayload>>
        ReadOccurrenceFeedAsync(
            Guid scopeId,
            ulong cursor,
            CancellationToken cancellationToken =
                default) =>
        ReadAsync<OccurrenceFeedPayload>(
            string.Create(
                CultureInfo.InvariantCulture,
                $"api/events/{scopeId:D}/occurrences/feed?cursor={cursor}"),
            cancellationToken);

    public Task<EventApiResult<EventPagePayload>>
        ReadHistoryAsync(
            Guid scopeId,
            string priority,
            DateTimeOffset from,
            DateTimeOffset to,
            int pageSize,
            ulong? after = null,
            ulong? upper = null,
            CancellationToken cancellationToken =
                default) =>
        ReadAsync<EventPagePayload>(
            EventQueryUri(
                scopeId,
                priority,
                from,
                to,
                pageSize,
                after,
                upper,
                counters: false),
            cancellationToken);

    public Task<EventApiResult<
        EventDispatcherCounters>>
        ReadHistoryCountersAsync(
            Guid scopeId,
            string priority,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken =
                default) =>
        ReadAsync<EventDispatcherCounters>(
            EventQueryUri(
                scopeId,
                priority,
                from,
                to,
                pageSize: 1,
                after: null,
                upper: null,
                counters: true),
            cancellationToken);

    public Task<EventApiResult<
        AlarmActionPayload>>
        AcknowledgeAsync(
            Guid scopeId,
            OccurrencePayload occurrence,
            CancellationToken cancellationToken =
                default) =>
        PostAsync(
            scopeId,
            occurrence.OccurrenceId,
            "acknowledge",
            new AlarmFacetActionRequest(
                occurrence.PointId,
                occurrence
                    .AcknowledgementVersion,
                IdempotencyKey()),
            cancellationToken);

    public Task<EventApiResult<
        AlarmActionPayload>>
        AssignAsync(
            Guid scopeId,
            OccurrencePayload occurrence,
            Guid assignedTo,
            CancellationToken cancellationToken =
                default) =>
        PostAsync(
            scopeId,
            occurrence.OccurrenceId,
            "assign",
            new AssignAlarmPayload(
                occurrence.PointId,
                assignedTo,
                occurrence.AssignmentVersion,
                IdempotencyKey()),
            cancellationToken);

    public Task<EventApiResult<
        AlarmActionPayload>>
        ShelveAsync(
            Guid scopeId,
            OccurrencePayload occurrence,
            DateTimeOffset until,
            string reason,
            CancellationToken cancellationToken =
                default) =>
        PostAsync(
            scopeId,
            occurrence.OccurrenceId,
            "shelve",
            new ShelveAlarmPayload(
                occurrence.PointId,
                until,
                reason,
                occurrence.ShelvingVersion,
                IdempotencyKey()),
            cancellationToken);

    public Task<EventApiResult<
        AlarmActionPayload>>
        UnshelveAsync(
            Guid scopeId,
            OccurrencePayload occurrence,
            CancellationToken cancellationToken =
                default) =>
        PostAsync(
            scopeId,
            occurrence.OccurrenceId,
            "unshelve",
            new AlarmFacetActionRequest(
                occurrence.PointId,
                occurrence.ShelvingVersion,
                IdempotencyKey()),
            cancellationToken);

    private async Task<EventApiResult<TPayload>>
        ReadAsync<TPayload>(
            string uri,
            CancellationToken cancellationToken)
    {
        using var response =
            await http.GetAsync(
                uri,
                cancellationToken);
        return await ResultAsync<TPayload>(
            response,
            cancellationToken);
    }

    private async Task<EventApiResult<
        AlarmActionPayload>> PostAsync<TRequest>(
            Guid scopeId,
            Guid occurrenceId,
            string action,
            TRequest request,
            CancellationToken cancellationToken)
    {
        using var response =
            await http.PostAsJsonAsync(
                $"api/alarms/{scopeId:D}/{occurrenceId:D}/{action}",
                request,
                cancellationToken);
        return await ResultAsync<
            AlarmActionPayload>(
                response,
                cancellationToken);
    }

    private static async Task<
        EventApiResult<TPayload>>
        ResultAsync<TPayload>(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return new EventApiResult<TPayload>(
                EventApiStatus.Available,
                await response.Content
                    .ReadFromJsonAsync<TPayload>(
                        cancellationToken));
        }

        return new EventApiResult<TPayload>(
            response.StatusCode switch
            {
                HttpStatusCode.Unauthorized =>
                    EventApiStatus.SessionExpired,
                HttpStatusCode.Forbidden =>
                    EventApiStatus.Forbidden,
                HttpStatusCode.Conflict =>
                    EventApiStatus.Conflict,
                _ => EventApiStatus.Unavailable,
            },
            default);
    }

    private static string EventQueryUri(
        Guid scopeId,
        string priority,
        DateTimeOffset from,
        DateTimeOffset to,
        int pageSize,
        ulong? after,
        ulong? upper,
        bool counters)
    {
        var uri = string.Create(
            CultureInfo.InvariantCulture,
            $"api/events/{scopeId:D}{(counters ? "/counters" : string.Empty)}" +
            $"?from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}" +
            $"&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}" +
            $"&pageSize={pageSize}");
        if (!string.Equals(
                priority,
                "All",
                StringComparison.Ordinal))
        {
            uri +=
                $"&priorities={Uri.EscapeDataString(priority)}";
        }

        if (after is not null &&
            upper is not null)
        {
            uri += string.Create(
                CultureInfo.InvariantCulture,
                $"&after={after.Value}&upper={upper.Value}");
        }

        return uri;
    }

    private static string IdempotencyKey() =>
        Guid.NewGuid().ToString("N");
}
