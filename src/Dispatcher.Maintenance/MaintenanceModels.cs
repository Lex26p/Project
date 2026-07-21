using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Maintenance;

public readonly record struct MaintenanceAssetId
{
    private readonly CanonicalId<MaintenanceAssetId> value;
    private MaintenanceAssetId(CanonicalId<MaintenanceAssetId> value) => this.value = value;
    public Guid Value => value.Value;
    public static MaintenanceAssetId New() => new(CanonicalId.New<MaintenanceAssetId>());
    public static MaintenanceAssetId From(Guid value) => new(CanonicalId.From<MaintenanceAssetId>(value));
}

public readonly record struct MaintenancePlanId
{
    private readonly CanonicalId<MaintenancePlanId> value;
    private MaintenancePlanId(CanonicalId<MaintenancePlanId> value) => this.value = value;
    public Guid Value => value.Value;
    public static MaintenancePlanId New() => new(CanonicalId.New<MaintenancePlanId>());
    public static MaintenancePlanId From(Guid value) => new(CanonicalId.From<MaintenancePlanId>(value));
}

public sealed record MaintenanceAssetSnapshot(
    MaintenanceAssetId AssetId,
    FacilityScopeId ScopeId,
    string Code,
    string Name,
    EquipmentId? EquipmentId,
    StateVersion Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum MaintenanceEquipmentLinkAction { Linked = 1, Unlinked = 2 }

public sealed record MaintenanceEquipmentLinkHistory(
    MaintenanceAssetId AssetId,
    StateVersion AssetVersion,
    MaintenanceEquipmentLinkAction Action,
    EquipmentId? EquipmentId,
    DateTimeOffset ChangedAt);

public sealed record CreateMaintenanceAssetRequest(
    MaintenanceAssetId AssetId,
    FacilityScopeId ScopeId,
    string Code,
    string Name,
    string IdempotencyKey);

public sealed record UpdateMaintenanceAssetRequest(
    MaintenanceAssetId AssetId,
    string Code,
    string Name,
    StateVersion ExpectedVersion,
    string IdempotencyKey);

public sealed record LinkMaintenanceEquipmentRequest(
    MaintenanceAssetId AssetId,
    EquipmentId EquipmentId,
    StateVersion ExpectedVersion,
    string IdempotencyKey);

public sealed record UnlinkMaintenanceEquipmentRequest(
    MaintenanceAssetId AssetId,
    StateVersion ExpectedVersion,
    string IdempotencyKey);

public enum MaintenanceCommandDisposition { Applied = 1, Replay = 2 }
public sealed record MaintenanceCommandResult(MaintenanceAssetSnapshot Asset, MaintenanceCommandDisposition Disposition);

public sealed record MaintenanceRecurrence
{
    public MaintenanceRecurrence(int intervalDays)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalDays);
        IntervalDays = intervalDays;
    }

    public int IntervalDays { get; }
}

public sealed record ApprovedMaintenancePlan
{
    public ApprovedMaintenancePlan(
        MaintenancePlanId planId,
        MaintenanceAssetId assetId,
        RevisionNumber revision,
        string title,
        DateOnly firstDueOn,
        MaintenanceRecurrence recurrence,
        DateOnly? effectiveThrough = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(recurrence);
        if (!revision.IsDefined || title.Length > 500 || effectiveThrough < firstDueOn)
        {
            throw new ArgumentException("Approved maintenance plan is invalid.");
        }

        PlanId = planId;
        AssetId = assetId;
        Revision = revision;
        Title = title.Trim();
        FirstDueOn = firstDueOn;
        Recurrence = recurrence;
        EffectiveThrough = effectiveThrough;
    }

    public MaintenancePlanId PlanId { get; }
    public MaintenanceAssetId AssetId { get; }
    public RevisionNumber Revision { get; }
    public string Title { get; }
    public DateOnly FirstDueOn { get; }
    public MaintenanceRecurrence Recurrence { get; }
    public DateOnly? EffectiveThrough { get; }
}

public sealed record MaintenanceCalendarQuery(DateOnly From, DateOnly To);

public sealed record MaintenanceForecastEntry(
    MaintenancePlanId PlanId,
    MaintenanceAssetId AssetId,
    RevisionNumber PlanRevision,
    string Title,
    DateOnly DueOn);

public static class MaintenancePermissions
{
    public static PermissionCode Read(FacilityScopeId scopeId) =>
        PermissionCode.From($"maintenance.scope.s{scopeId.Value:N}.read");

    public static PermissionCode ManageAsset(FacilityScopeId scopeId) =>
        PermissionCode.From($"maintenance.scope.s{scopeId.Value:N}.asset.manage");
}
