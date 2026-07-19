using Dispatcher.Configuration;
using Dispatcher.Equipment;
using Dispatcher.Platform;
using Dispatcher.Semantics;

namespace Dispatcher.Server;

public sealed class EquipmentStagingService
{
    private readonly EquipmentStagingStore staging;
    private readonly EquipmentService equipment;
    private readonly InitialConfigurationService configuration;
    private readonly StagingSecretProtector protector;
    private readonly IWallClock clock;

    public EquipmentStagingService(
        EquipmentStagingStore staging,
        EquipmentService equipment,
        InitialConfigurationService configuration,
        StagingSecretProtector protector,
        IWallClock clock)
    {
        this.staging = staging;
        this.equipment = equipment;
        this.configuration = configuration;
        this.protector = protector;
        this.clock = clock;
    }

    public async Task<IReadOnlyList<StagingRowResult>> ProcessAsync(
        SessionSnapshot? session,
        IReadOnlyCollection<StagingRowInput> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var results = new List<StagingRowResult>(rows.Count);
        foreach (var row in rows)
        {
            results.Add(await ProcessRowAsync(session, row, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async Task<StagingRowResult> ReconcileAsync(
        SessionSnapshot? session,
        Dispatcher.Facilities.FacilityScopeId scopeId,
        Guid rowId,
        CancellationToken cancellationToken = default)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(
            session,
            EquipmentPermissions.Write(scopeId),
            clock);
        if (authorization.IsFailure)
        {
            return Failure(rowId, default, "row", authorization.Error!);
        }

        var workItem = await staging.ReadWorkItemAsync(scopeId, rowId, cancellationToken).ConfigureAwait(false);
        return workItem.IsFailure
            ? Failure(rowId, default, "row", workItem.Error!)
            : await ContinueAsync(session, authorization.Value, workItem.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StagingRowResult> ProcessRowAsync(
        SessionSnapshot? session,
        StagingRowInput row,
        CancellationToken cancellationToken)
    {
        var errors = EquipmentStagingTools.Validate(row);
        if (errors.Count > 0)
        {
            return new StagingRowResult(row.RowId, row.EquipmentId, null, errors);
        }

        var authorization = SessionAuthorization.AuthorizeAccess(
            session,
            EquipmentPermissions.Write(row.ScopeId),
            clock);
        if (authorization.IsFailure)
        {
            return Failure(row.RowId, row.EquipmentId, "row", authorization.Error!);
        }

        var reserved = await staging.ReserveAsync(authorization.Value, row, protector, cancellationToken)
            .ConfigureAwait(false);
        if (reserved.IsFailure)
        {
            return Failure(row.RowId, row.EquipmentId, "row", reserved.Error!);
        }

        var workItem = await staging.ReadWorkItemAsync(row.ScopeId, row.RowId, cancellationToken).ConfigureAwait(false);
        return workItem.IsFailure
            ? Failure(row.RowId, row.EquipmentId, "row", workItem.Error!)
            : await ContinueAsync(session, authorization.Value, workItem.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StagingRowResult> ContinueAsync(
        SessionSnapshot? session,
        AuthorizedAccess authorization,
        StagingWorkItem item,
        CancellationToken cancellationToken)
    {
        var snapshot = item.Snapshot;
        if (snapshot.State == StagingRowState.Reserved)
        {
            var accepted = await equipment.EnsureEquipmentAsync(
                session,
                new CreateEquipment(
                    snapshot.EquipmentId,
                    snapshot.ScopeId,
                    snapshot.LocationId,
                    snapshot.Code,
                    snapshot.Name),
                cancellationToken).ConfigureAwait(false);
            if (accepted.IsFailure)
            {
                return Failure(snapshot.RowId, snapshot.EquipmentId, "equipment", accepted.Error!);
            }

            var advanced = await staging.AdvanceAsync(
                authorization,
                snapshot.ScopeId,
                snapshot.RowId,
                StagingRowState.Reserved,
                StagingRowState.EquipmentAccepted,
                cancellationToken).ConfigureAwait(false);
            if (advanced.IsFailure)
            {
                return Failure(snapshot.RowId, snapshot.EquipmentId, "row", advanced.Error!);
            }

            snapshot = advanced.Value;
        }

        if (snapshot.State == StagingRowState.EquipmentAccepted)
        {
            var obligation = await configuration.AcceptAsync(
                session,
                new AcceptInitialConfiguration(
                    snapshot.RowId,
                    item.RequestFingerprint,
                    snapshot.ScopeId,
                    snapshot.EquipmentId,
                    snapshot.Protocol,
                    item.FormDataJson,
                    item.ProtectedSecret),
                cancellationToken).ConfigureAwait(false);
            if (obligation.IsFailure)
            {
                return new StagingRowResult(
                    snapshot.RowId,
                    snapshot.EquipmentId,
                    snapshot.State,
                    [new StagingFieldError(
                        "initial_configuration",
                        obligation.Error!.Code.Value,
                        obligation.Error.Message)]);
            }

            var advanced = await staging.AdvanceAsync(
                authorization,
                snapshot.ScopeId,
                snapshot.RowId,
                StagingRowState.EquipmentAccepted,
                StagingRowState.Created,
                cancellationToken).ConfigureAwait(false);
            if (advanced.IsFailure)
            {
                return Failure(snapshot.RowId, snapshot.EquipmentId, "row", advanced.Error!);
            }

            snapshot = advanced.Value;
        }

        return new StagingRowResult(snapshot.RowId, snapshot.EquipmentId, snapshot.State, []);
    }

    private static StagingRowResult Failure(
        Guid rowId,
        EquipmentId equipmentId,
        string field,
        OperationError error) => new(
        rowId,
        equipmentId,
        null,
        [new StagingFieldError(field, error.Code.Value, error.Message)]);
}
