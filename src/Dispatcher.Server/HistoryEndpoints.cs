using Dispatcher.Core;
using Dispatcher.History;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Dispatcher.Server;

public static class HistoryPermissions
{
    public static PermissionCode ReadRange { get; } = PermissionCode.From("history.range.read");
}

public sealed class AuthorizedHistoryReader
{
    private readonly HistoryStore store;
    private readonly IWallClock clock;

    public AuthorizedHistoryReader(HistoryStore store, IWallClock clock)
    {
        this.store = store;
        this.clock = clock;
    }

    public Task<Result<HistoryRangePage>> QueryRangeAsync(
        SessionSnapshot? session,
        HistoryRangeRequest request,
        CancellationToken cancellationToken)
    {
        var authorization = Authorize(session, request.PointId);
        return authorization.IsSuccess
            ? store.QueryRangeAsync(request, cancellationToken)
            : Task.FromResult(Result.Failure<HistoryRangePage>(authorization.Error!));
    }

    public Task<Result<HistoryAggregateSeries>> QueryAggregatesAsync(
        SessionSnapshot? session,
        HistoryRangeRequest request,
        HistoryResolutionPolicy policy,
        CancellationToken cancellationToken)
    {
        var authorization = Authorize(session, request.PointId);
        return authorization.IsSuccess
            ? store.QueryAggregatesAsync(request, policy, cancellationToken)
            : Task.FromResult(Result.Failure<HistoryAggregateSeries>(authorization.Error!));
    }

    private Result Authorize(SessionSnapshot? session, PointId pointId)
    {
        var access = SessionAuthorization.AuthorizeAccess(session, HistoryPermissions.ReadRange, clock);
        if (access.IsFailure)
        {
            return Result.Failure(access.Error!);
        }

        return access.Value.Session.Permissions.Allows(RuntimePermissions.ReadPoint(pointId))
            ? Result.Success()
            : Result.Failure(new OperationError(
                ErrorCode.From("permission.denied"),
                "Point permission is required for History range access."));
    }
}

public static class HistoryEndpoints
{
    public static IServiceCollection AddHistoryServer(
        this IServiceCollection services,
        string connectionString,
        string databaseRole,
        HistoryQueryLimits queryLimits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(sp => new HistoryStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            databaseRole,
            sp.GetRequiredService<IWallClock>(),
            queryLimits));
        services.AddSingleton<AuthorizedHistoryReader>();
        return services;
    }

    public static IEndpointRouteBuilder MapHistoryServer(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/history");
        group.MapGet("/range", async Task<IResult> (
            Guid scopeId,
            Guid sourceId,
            Guid pointId,
            DateTimeOffset from,
            DateTimeOffset to,
            int pageSize,
            ulong? after,
            ulong? upper,
            HttpContext context,
            RequestSessionResolver sessions,
            AuthorizedHistoryReader history,
            CancellationToken cancellationToken) =>
        {
            var cursor = after is null || upper is null
                ? null
                : new HistoryQueryCursor(
                    new HistoryStreamPosition(after.Value),
                    new HistoryStreamPosition(upper.Value));
            var result = await history.QueryRangeAsync(
                sessions.Resolve(context),
                new HistoryRangeRequest(
                    RuntimeScopeId.From(scopeId),
                    SourceId.From(sourceId),
                    PointId.From(pointId),
                    from,
                    to,
                    pageSize,
                    cursor),
                cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(ToPayload(result.Value)) : Problem(result.Error!);
        });
        group.MapGet("/aggregate", async Task<IResult> (
            Guid scopeId,
            Guid sourceId,
            Guid pointId,
            DateTimeOffset from,
            DateTimeOffset to,
            int resolutionSeconds,
            HttpContext context,
            RequestSessionResolver sessions,
            AuthorizedHistoryReader history,
            CancellationToken cancellationToken) =>
        {
            var result = await history.QueryAggregatesAsync(
                sessions.Resolve(context),
                new HistoryRangeRequest(
                    RuntimeScopeId.From(scopeId),
                    SourceId.From(sourceId),
                    PointId.From(pointId),
                    from,
                    to,
                    PageSize: 1),
                new HistoryResolutionPolicy(1, TimeSpan.FromSeconds(resolutionSeconds)),
                cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(ToPayload(result.Value)) : Problem(result.Error!);
        });
        return endpoints;
    }

    private static HistoryRangePayload ToPayload(HistoryRangePage page) => new(
        page.Records.Select(record => new HistoryRecordPayload(
            record.Position.Value,
            record.Sample is null ? "gap" : "sample",
            record.Sample?.Value.Value,
            record.Sample?.Unit.Symbol,
            record.Sample?.Quality.ToString(),
            record.Sample?.Freshness.ToString(),
            record.Sample?.SourceTimestamp.Value,
            record.Sample?.IsLate,
            record.Sample?.IsOutOfOrder,
            record.Gap?.FirstSourcePosition,
            record.Gap?.LastSourcePosition,
            record.Gap?.Reason)).ToArray(),
        page.UpperBound.Value,
        page.NextCursor?.After.Value,
        page.NextCursor?.UpperBound.Value);

    private static HistoryAggregatePayload ToPayload(HistoryAggregateSeries series) => new(
        series.Policy.Version,
        series.Policy.Resolution.TotalSeconds,
        series.Buckets.Select(bucket => new HistoryBucketPayload(
            bucket.FromInclusive,
            bucket.ToExclusive,
            bucket.Count,
            bucket.Average,
            bucket.Minimum,
            bucket.Maximum,
            bucket.Quality.ToString(),
            bucket.Freshness.ToString(),
            bucket.HasGap)).ToArray());

    private static IResult Problem(OperationError error) => Results.Problem(
        statusCode: error.Code.Value switch
        {
            "session.anonymous" or "session.revoked" or "session.expired" => StatusCodes.Status401Unauthorized,
            "permission.denied" => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        },
        title: error.Code.Value,
        detail: error.Message);
}

public sealed record HistoryRecordPayload(
    ulong Position,
    string Kind,
    long? Value,
    string? Unit,
    string? Quality,
    string? Freshness,
    DateTimeOffset? SourceTimestamp,
    bool? IsLate,
    bool? IsOutOfOrder,
    ulong? GapFirstSourcePosition,
    ulong? GapLastSourcePosition,
    string? GapReason);

public sealed record HistoryRangePayload(
    IReadOnlyList<HistoryRecordPayload> Records,
    ulong UpperBound,
    ulong? NextAfter,
    ulong? NextUpper);

public sealed record HistoryBucketPayload(
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    long Count,
    double Average,
    long Minimum,
    long Maximum,
    string Quality,
    string Freshness,
    bool HasGap);

public sealed record HistoryAggregatePayload(
    int PolicyVersion,
    double ResolutionSeconds,
    IReadOnlyList<HistoryBucketPayload> Buckets);
