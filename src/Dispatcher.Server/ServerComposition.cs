using Dispatcher.Semantics;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Dispatcher.Server;

public static class ServerComposition
{
    public static IServiceCollection AddDispatcherServer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<TestSessionBridgeOptions>(
            configuration.GetSection(TestSessionBridgeOptions.SectionName));
        services.AddSingleton<IWallClock>(SystemClock.Instance);
        services.AddSingleton<RuntimeRegistry>();
        services.AddSingleton<SessionDirectory>();
        services.AddSingleton<AuthorizedRuntimeReader>();
        services.AddSingleton<RequestSessionResolver>();
        services.AddSingleton<RealtimeSubscriptionStore>();
        services.AddSignalR();
        return services;
    }

    public static IEndpointRouteBuilder MapDispatcherServer(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
            "/api/runtime/{scopeId:guid}/snapshot",
            Results<Ok<RuntimeSnapshotPayload>, ProblemHttpResult> (
                Guid scopeId,
                HttpContext context,
                RequestSessionResolver sessions,
                AuthorizedRuntimeReader reader) =>
            {
                var result = reader.ReadSnapshot(sessions.Resolve(context), Core.RuntimeScopeId.From(scopeId));
                return result.IsSuccess
                    ? TypedResults.Ok(result.Value.Payload)
                    : TypedResults.Problem(
                        statusCode: StatusCode(result.Error!.Code.Value),
                        title: result.Error.Code.Value,
                        detail: result.Error.Message);
            });
        endpoints.MapHub<RuntimeRealtimeHub>("/hubs/runtime");
        return endpoints;
    }

    private static int StatusCode(string code) => code switch
    {
        "session.anonymous" or "session.revoked" or "session.expired" => StatusCodes.Status401Unauthorized,
        "permission.denied" => StatusCodes.Status403Forbidden,
        "runtime.scope_not_found" => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status400BadRequest,
    };
}
