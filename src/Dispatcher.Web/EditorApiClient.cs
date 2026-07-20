using System.Net;
using System.Net.Http.Json;

namespace Dispatcher.Web;

public sealed class EditorApiClient(HttpClient http)
{
    public Task<DashboardEditorDraftPayload?> ReadDashboardAsync(Guid id, CancellationToken token = default) =>
        ReadAsync<DashboardEditorDraftPayload>($"api/dashboard-editor/{id:D}", token);

    public Task<MimicEditorDraftPayload?> ReadMimicAsync(Guid id, CancellationToken token = default) =>
        ReadAsync<MimicEditorDraftPayload>($"api/mimic-editor/{id:D}", token);

    public Task<EditorRevisionPayload> SaveDashboardAsync(
        Guid id, SaveDashboardEditorRequest request, CancellationToken token = default) =>
        SendRevisionAsync(HttpMethod.Put, $"api/dashboard-editor/{id:D}/draft", request, token);

    public Task<EditorRevisionPayload> SaveMimicAsync(
        Guid id, SaveMimicEditorRequest request, CancellationToken token = default) =>
        SendRevisionAsync(HttpMethod.Put, $"api/mimic-editor/{id:D}/draft", request, token);

    public Task<EditorRevisionPayload> ValidateDashboardAsync(
        Guid id, EditorRevisionRequest request, CancellationToken token = default) =>
        SendRevisionAsync(HttpMethod.Post, $"api/dashboard-editor/{id:D}/validate", request, token);

    public Task<EditorRevisionPayload> PublishDashboardAsync(
        Guid id, EditorRevisionRequest request, CancellationToken token = default) =>
        SendRevisionAsync(HttpMethod.Post, $"api/dashboard-editor/{id:D}/publish", request, token);

    public Task<EditorRevisionPayload> ValidateMimicAsync(
        Guid id, EditorRevisionRequest request, CancellationToken token = default) =>
        SendRevisionAsync(HttpMethod.Post, $"api/mimic-editor/{id:D}/validate", request, token);

    public Task<EditorRevisionPayload> PublishMimicAsync(
        Guid id, EditorRevisionRequest request, CancellationToken token = default) =>
        SendRevisionAsync(HttpMethod.Post, $"api/mimic-editor/{id:D}/publish", request, token);

    public async Task<MimicPreviewPayload> PreviewMimicAsync(
        Guid id, MimicEditorDocumentPayload document, CancellationToken token = default)
    {
        var response = await http.PostAsJsonAsync($"api/mimic-editor/{id:D}/preview", document, token);
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<MimicPreviewPayload>(token)
            ?? throw new InvalidOperationException("Mimic preview response was empty.");
    }

    private async Task<T?> ReadAsync<T>(string uri, CancellationToken token)
    {
        var response = await http.GetAsync(uri, token);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return default;
        }

        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<T>(token);
    }

    private async Task<EditorRevisionPayload> SendRevisionAsync<T>(
        HttpMethod method, string uri, T request, CancellationToken token)
    {
        using var message = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(request) };
        var response = await http.SendAsync(message, token);
        await EnsureAsync(response);
        return await response.Content.ReadFromJsonAsync<EditorRevisionPayload>(token)
            ?? throw new InvalidOperationException("Editor revision response was empty.");
    }

    private static Task EnsureAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new EditorAccessDeniedException();
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new EditorConflictException();
        }

        response.EnsureSuccessStatusCode();
        return Task.CompletedTask;
    }
}
