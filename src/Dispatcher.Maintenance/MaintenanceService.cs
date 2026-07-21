using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Maintenance;

public sealed class MaintenanceService
{
    private readonly MaintenanceStore store;
    private readonly EquipmentStore equipmentStore;
    private readonly IWallClock clock;

    public MaintenanceService(MaintenanceStore store, EquipmentStore equipmentStore, IWallClock clock)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.equipmentStore = equipmentStore ?? throw new ArgumentNullException(nameof(equipmentStore));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<Result<MaintenanceCommandResult>> CreateAssetAsync(
        SessionSnapshot? session, CreateMaintenanceAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authorization = SessionAuthorization.AuthorizeMutation(
            session, MaintenancePermissions.ManageAsset(request.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenanceCommandResult>(authorization.Error!)
            : await store.CreateAsync(authorization.Value, request, cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<MaintenanceCommandResult>> UpdateAssetAsync(
        SessionSnapshot? session, UpdateMaintenanceAssetRequest request,
        CancellationToken cancellationToken = default) =>
        MutateExistingAsync(session, request.AssetId, (authorization, token) =>
            store.UpdateAsync(authorization, request, token), cancellationToken);

    public async Task<Result<MaintenanceCommandResult>> LinkEquipmentAsync(
        SessionSnapshot? session, LinkMaintenanceEquipmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var asset = await store.ReadAssetAsync(request.AssetId, cancellationToken).ConfigureAwait(false);
        if (asset is null)
        {
            return Failure<MaintenanceCommandResult>("maintenance.asset_not_found", "Maintenance asset was not found.");
        }

        var authorization = SessionAuthorization.AuthorizeMutation(
            session, MaintenancePermissions.ManageAsset(asset.ScopeId), clock);
        if (authorization.IsFailure)
        {
            return Result.Failure<MaintenanceCommandResult>(authorization.Error!);
        }

        var equipmentAuthorization = SessionAuthorization.AuthorizeAccess(
            session, EquipmentPermissions.Read(asset.ScopeId), clock);
        if (equipmentAuthorization.IsFailure)
        {
            return Result.Failure<MaintenanceCommandResult>(equipmentAuthorization.Error!);
        }

        var equipment = await equipmentStore.ReadEquipmentAsync(request.EquipmentId, cancellationToken).ConfigureAwait(false);
        if (equipment is null || equipment.ScopeId != asset.ScopeId)
        {
            return Failure<MaintenanceCommandResult>(
                "maintenance.equipment_not_found", "Equipment is not available in the maintenance asset scope.");
        }

        return await store.LinkEquipmentAsync(authorization.Value, request, cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<MaintenanceCommandResult>> UnlinkEquipmentAsync(
        SessionSnapshot? session, UnlinkMaintenanceEquipmentRequest request,
        CancellationToken cancellationToken = default) =>
        MutateExistingAsync(session, request.AssetId, (authorization, token) =>
            store.UnlinkEquipmentAsync(authorization, request, token), cancellationToken);

    public async Task<Result<IReadOnlyList<MaintenanceAssetSnapshot>>> ReadAssetsAsync(
        SessionSnapshot? session, FacilityScopeId scopeId, CancellationToken cancellationToken = default)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(session, MaintenancePermissions.Read(scopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<IReadOnlyList<MaintenanceAssetSnapshot>>(authorization.Error!)
            : Result.Success(await store.ReadAssetsAsync(scopeId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<Result<IReadOnlyList<MaintenanceEquipmentLinkHistory>>> ReadLinkHistoryAsync(
        SessionSnapshot? session, MaintenanceAssetId assetId, CancellationToken cancellationToken = default)
    {
        var asset = await store.ReadAssetAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (asset is null)
        {
            return Failure<IReadOnlyList<MaintenanceEquipmentLinkHistory>>(
                "maintenance.asset_not_found", "Maintenance asset was not found.");
        }

        var authorization = SessionAuthorization.AuthorizeAccess(session, MaintenancePermissions.Read(asset.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<IReadOnlyList<MaintenanceEquipmentLinkHistory>>(authorization.Error!)
            : Result.Success(await store.ReadLinkHistoryAsync(assetId, cancellationToken).ConfigureAwait(false));
    }

    private async Task<Result<MaintenanceCommandResult>> MutateExistingAsync(
        SessionSnapshot? session,
        MaintenanceAssetId assetId,
        Func<AuthorizedMutation, CancellationToken, Task<Result<MaintenanceCommandResult>>> mutation,
        CancellationToken cancellationToken)
    {
        var asset = await store.ReadAssetAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (asset is null)
        {
            return Failure<MaintenanceCommandResult>("maintenance.asset_not_found", "Maintenance asset was not found.");
        }

        var authorization = SessionAuthorization.AuthorizeMutation(
            session, MaintenancePermissions.ManageAsset(asset.ScopeId), clock);
        return authorization.IsFailure
            ? Result.Failure<MaintenanceCommandResult>(authorization.Error!)
            : await mutation(authorization.Value, cancellationToken).ConfigureAwait(false);
    }

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));
}
