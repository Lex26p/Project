using System.Net;
using Dispatcher.Web;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class WorkspaceApiClientTests
{
    [Theory]
    [InlineData(HttpStatusCode.NoContent, RouteAccess.Allowed)]
    [InlineData(HttpStatusCode.Unauthorized, RouteAccess.SessionExpired)]
    [InlineData(HttpStatusCode.Forbidden, RouteAccess.Denied)]
    [InlineData(HttpStatusCode.NotFound, RouteAccess.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, RouteAccess.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, RouteAccess.Unavailable)]
    public async Task AccessResponseKeepsAuthorizationAndAvailabilitySeparate(
        HttpStatusCode statusCode,
        RouteAccess expected)
    {
        using var http =
            new HttpClient(
                new StatusCodeHandler(statusCode))
            {
                BaseAddress =
                    new Uri("http://dispatcher.test/"),
            };
        var api = new WorkspaceApiClient(http);

        var actual =
            await api.CheckAccessAsync("/home");

        Assert.Equal(expected, actual);
    }

    private sealed class StatusCodeHandler(
        HttpStatusCode statusCode) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage>
            SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                new HttpResponseMessage(statusCode)
                {
                    RequestMessage = request,
                });
    }
}
