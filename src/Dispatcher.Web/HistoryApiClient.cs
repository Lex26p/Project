using System.Globalization;
using System.Net.Http.Json;

namespace Dispatcher.Web;

public sealed class HistoryApiClient
{
    private readonly HttpClient http;

    public HistoryApiClient(HttpClient http)
    {
        this.http = http;
    }

    public async Task<HistoryAggregatePayload?> ReadAggregatesAsync(
        Guid scopeId,
        Guid sourceId,
        Guid pointId,
        DateTimeOffset from,
        DateTimeOffset to,
        int resolutionSeconds,
        CancellationToken cancellationToken = default)
    {
        var uri = string.Create(
            CultureInfo.InvariantCulture,
            $"api/history/aggregate?scopeId={scopeId:D}&sourceId={sourceId:D}&pointId={pointId:D}" +
            $"&from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}" +
            $"&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}" +
            $"&resolutionSeconds={resolutionSeconds}");
        var response = await http.GetAsync(uri, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<HistoryAggregatePayload>(cancellationToken)
            : null;
    }

    public async Task<HistoryRangePayload?>
        ReadRangeAsync(
            Guid scopeId,
            Guid sourceId,
            Guid pointId,
            DateTimeOffset from,
            DateTimeOffset to,
            int pageSize,
            ulong? after = null,
            ulong? upper = null,
            CancellationToken cancellationToken =
                default)
    {
        var uri = string.Create(
            CultureInfo.InvariantCulture,
            $"api/history/range?scopeId={scopeId:D}&sourceId={sourceId:D}&pointId={pointId:D}" +
            $"&from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}" +
            $"&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}" +
            $"&pageSize={pageSize}");
        if (after is not null &&
            upper is not null)
        {
            uri += string.Create(
                CultureInfo.InvariantCulture,
                $"&after={after.Value}&upper={upper.Value}");
        }

        using var response =
            await http.GetAsync(
                uri,
                cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content
                .ReadFromJsonAsync<
                    HistoryRangePayload>(
                    cancellationToken)
            : null;
    }
}
