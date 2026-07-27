using Dispatcher.Core;
using Dispatcher.Semantics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

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
        services.AddSingleton<SessionDirectory>();
        services.AddSingleton<RequestSessionResolver>();
        services.AddSingleton<RealtimeSubscriptionStore>();
        var connectionString = configuration.GetConnectionString("Dispatcher");
        var publishedReadRole = configuration["Dispatcher:Core:PublishedReadRole"];
        var maxSnapshotPoints = configuration.GetValue<int?>(
            "Dispatcher:Core:MaxSnapshotPoints");
        var maxDeltaChanges = configuration.GetValue<int?>(
            "Dispatcher:Core:MaxDeltaChanges");
        var currentConfigured =
            !string.IsNullOrWhiteSpace(connectionString) &&
            !string.IsNullOrWhiteSpace(publishedReadRole) &&
            maxSnapshotPoints > 0 &&
            maxDeltaChanges > 0;
        if (currentConfigured)
        {
            services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString!));
            services.AddSingleton(sp => new CoreRuntimePublishedReader(
                sp.GetRequiredService<NpgsqlDataSource>(),
                publishedReadRole!,
                new PublishedCurrentReadLimits(
                    maxSnapshotPoints!.Value,
                    maxDeltaChanges!.Value)));
            services.AddSingleton<AuthorizedRuntimeReader>();
        }
        else
        {
            services.AddSingleton(sp => new AuthorizedRuntimeReader(
                sp.GetRequiredService<IWallClock>()));
        }
        services.AddSignalR();
        return services;
    }

    public static IEndpointRouteBuilder MapDispatcherServer(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
            "/api/runtime/{scopeId:guid}/snapshot",
            async Task<Results<Ok<RuntimeSnapshotPayload>, ProblemHttpResult>> (
                Guid scopeId,
                HttpContext context,
                RequestSessionResolver sessions,
                AuthorizedRuntimeReader reader,
                CancellationToken cancellationToken) =>
            {
                var result = await reader.ReadSnapshotAsync(
                    sessions.Resolve(context),
                    RuntimeScopeId.From(scopeId),
                    cancellationToken: cancellationToken);
                return result.IsSuccess
                    ? TypedResults.Ok(result.Value.Payload)
                    : TypedResults.Problem(
                        statusCode: StatusCode(result.Error!.Code.Value),
                        title: result.Error.Code.Value,
                        detail: result.Error.Message);
            });
        endpoints.MapGet(
            "/api/runtime/{scopeId:guid}/readiness",
            async Task<Results<Ok<RuntimeReadinessPayload>, ProblemHttpResult>> (
                Guid scopeId,
                HttpContext context,
                RequestSessionResolver sessions,
                AuthorizedRuntimeReader reader,
                CancellationToken cancellationToken) =>
            {
                var result = await reader.ReadReadinessAsync(
                    sessions.Resolve(context),
                    RuntimeScopeId.From(scopeId),
                    cancellationToken);
                return result.IsSuccess
                    ? TypedResults.Ok(result.Value)
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
        "runtime.scope_not_ready" or
        "runtime.current_unavailable" or
        "runtime.query_limit_exceeded" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };
}
