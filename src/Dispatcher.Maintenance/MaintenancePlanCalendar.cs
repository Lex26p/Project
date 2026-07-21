namespace Dispatcher.Maintenance;

public static class MaintenancePlanCalendar
{
    public static IReadOnlyList<MaintenanceForecastEntry> Query(
        IReadOnlyCollection<ApprovedMaintenancePlan> approvedPlans,
        IReadOnlyCollection<MaintenanceAssetSnapshot> assets,
        MaintenanceCalendarQuery query)
    {
        ArgumentNullException.ThrowIfNull(approvedPlans);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(query);
        if (query.To < query.From)
        {
            throw new ArgumentException("Calendar range is reversed.", nameof(query));
        }

        var assetIds = assets.Select(asset => asset.AssetId).ToHashSet();
        if (approvedPlans.Any(plan => !assetIds.Contains(plan.AssetId)))
        {
            throw new ArgumentException("Approved plan references an unavailable maintenance asset.", nameof(approvedPlans));
        }

        var entries = new List<MaintenanceForecastEntry>();
        foreach (var plan in approvedPlans.OrderBy(value => value.PlanId.Value))
        {
            var firstOffset = Math.Max(0, query.From.DayNumber - plan.FirstDueOn.DayNumber);
            var steps = (firstOffset + plan.Recurrence.IntervalDays - 1) / plan.Recurrence.IntervalDays;
            var dueOn = plan.FirstDueOn.AddDays(checked(steps * plan.Recurrence.IntervalDays));
            while (dueOn <= query.To && (plan.EffectiveThrough is null || dueOn <= plan.EffectiveThrough.Value))
            {
                entries.Add(new MaintenanceForecastEntry(
                    plan.PlanId, plan.AssetId, plan.Revision, plan.Title, dueOn));
                dueOn = dueOn.AddDays(plan.Recurrence.IntervalDays);
            }
        }

        return entries.OrderBy(value => value.DueOn).ThenBy(value => value.PlanId.Value).ToArray();
    }
}
