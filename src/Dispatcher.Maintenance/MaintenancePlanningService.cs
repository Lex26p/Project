using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Maintenance;

public sealed class MaintenancePlanningService
{
    private readonly MaintenanceSchedulerStore store;
    private readonly IWallClock clock;
    private readonly MaintenanceQueryLimits limits;

    public MaintenancePlanningService(
        MaintenanceSchedulerStore store,
        IWallClock clock,
        MaintenanceQueryLimits? limits = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.limits = limits ?? MaintenanceQueryLimits.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.limits.MaximumPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.limits.MaximumCalendarRangeDays);
    }

    public async Task<Result<MaintenancePlanSnapshot>> GetPlanAsync(
        SessionSnapshot? session,
        MaintenancePlanId planId,
        CancellationToken cancellationToken = default)
    {
        var plan = await store.ReadPlanAsync(planId, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return Failure<MaintenancePlanSnapshot>(
                "maintenance.plan_not_found", "Maintenance plan was not found.");
        }

        var authorization = SessionAuthorization.AuthorizeAccess(
            session, MaintenancePermissions.Read(plan.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenancePlanSnapshot>(authorization.Error!)
            : Result.Success(plan);
    }

    public Task<Result<MaintenanceForecastPage>> QueryForecastAsync(
        SessionSnapshot? session,
        MaintenanceForecastQuery query,
        CancellationToken cancellationToken = default) =>
        QueryAsync(session, query, cancellationToken);

    public Task<Result<MaintenanceForecastPage>> QueryCalendarAsync(
        SessionSnapshot? session,
        MaintenanceForecastQuery query,
        CancellationToken cancellationToken = default) =>
        QueryAsync(session, query, cancellationToken);

    private async Task<Result<MaintenanceForecastPage>> QueryAsync(
        SessionSnapshot? session,
        MaintenanceForecastQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rangeDays = query.To.DayNumber - query.From.DayNumber;
        if (query.PageSize <= 0 ||
            query.PageSize > limits.MaximumPageSize ||
            rangeDays < 0 ||
            rangeDays > limits.MaximumCalendarRangeDays ||
            (query.AfterDueOn is null) != (query.AfterObligationId is null) ||
            query.AfterDueOn < query.From ||
            query.AfterDueOn > query.To)
        {
            return Failure<MaintenanceForecastPage>(
                "maintenance.forecast_query", "Maintenance forecast query is invalid.");
        }

        var authorization = SessionAuthorization.AuthorizeAccess(
            session, MaintenancePermissions.Read(query.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenanceForecastPage>(authorization.Error!)
            : Result.Success(
                await store.QueryForecastAsync(query, cancellationToken).ConfigureAwait(false));
    }

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));
}
