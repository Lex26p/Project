using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Npgsql;
using NpgsqlTypes;

namespace Dispatcher.Maintenance;

public sealed class MaintenanceWorkStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;

    public MaintenanceWorkStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        if (databaseRole.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new ArgumentException("Invalid PostgreSQL role name.", nameof(databaseRole));
        }

        this.databaseRole = databaseRole;
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>> CreateRequestAsync(
        AuthorizedMutation authorization,
        MaintenanceAssetSnapshot asset,
        CreateMaintenanceRequest request,
        CancellationToken cancellationToken = default) =>
        CreateRequestCoreAsync(authorization, asset, request.RequestId, request.Summary, request.IdempotencyKey, null, cancellationToken);

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>> CreateRequestFromEventAsync(
        AuthorizedMutation authorization,
        MaintenanceAssetSnapshot asset,
        CreateMaintenanceRequestFromEvent request,
        CancellationToken cancellationToken = default) =>
        CreateRequestCoreAsync(
            authorization, asset, request.RequestId, request.Summary, request.IdempotencyKey,
            new EventSourceDto(
                request.Event.EventId.Value, request.Event.OccurrenceId.Value, request.Event.ScopeId.Value,
                request.Event.PointId.Value, $"/events/{request.Event.EventId.Value}",
                ["events.dispatcher.read", $"runtime.point.p{request.Event.PointId.Value:N}.read"]),
            cancellationToken);

    public async Task<Result<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>> ApproveRequestAsync(
        AuthorizedMutation authorization,
        ApproveMaintenanceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fingerprint = Fingerprint(JsonSerializer.Serialize(new
        {
            RequestId = request.RequestId.Value,
            ExpectedVersion = request.ExpectedVersion.Value,
        }));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var replay = await ReadReceiptAsync<RequestDto>(
            connection, transaction, request.IdempotencyKey, "approve-request", fingerprint, cancellationToken).ConfigureAwait(false);
        if (replay.IsFailure)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>(replay.Error!);
        }

        if (replay.Value.Value is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Replayed(replay.Value.Value.ToModel());
        }

        var current = await ReadRequestDtoAsync(connection, transaction, request.RequestId, true, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return Failure<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>(
                "maintenance.request_not_found", "Maintenance request was not found.");
        }

        var contract = ValidateTransition(
            authorization, MaintenanceWorkPermissions.Manage(FacilityScopeId.From(current.ScopeId)),
            request.ExpectedVersion, current.Version, request.IdempotencyKey);
        if (contract.IsFailure || current.State != (int)MaintenanceRequestState.Submitted)
        {
            return contract.IsFailure
                ? Result.Failure<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>(contract.Error!)
                : Failure<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>(
                    "maintenance.request_state", "Only a submitted request can be approved.");
        }

        var now = UtcNow();
        var next = current with
        {
            State = (int)MaintenanceRequestState.Approved,
            Version = request.ExpectedVersion.Next().Value,
            UpdatedAt = now,
        };
        await UpdateRequestAsync(connection, transaction, next, cancellationToken).ConfigureAwait(false);
        await WriteReceiptAndAuditAsync(
            connection, transaction, request.IdempotencyKey, "approve-request", fingerprint, "request", next,
            authorization, next.ScopeId, next.RequestId, next.Version, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Applied(next.ToModel());
    }

    public async Task<Result<MaintenanceWorkCommandResult<MaintenanceDefectSnapshot>>> CreateDefectAsync(
        AuthorizedMutation authorization,
        MaintenanceAssetSnapshot asset,
        CreateMaintenanceDefect request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateCreate(
            authorization, MaintenanceWorkPermissions.Manage(asset.ScopeId), request.Summary, request.IdempotencyKey);
        if (validation.IsFailure)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceDefectSnapshot>>(validation.Error!);
        }

        var now = UtcNow();
        var dto = new DefectDto(
            request.DefectId.Value, asset.AssetId.Value, asset.ScopeId.Value, request.Summary.Trim(),
            (int)MaintenanceDefectState.Reported, StateVersion.Initial.Value, now, now);
        var fingerprint = Fingerprint(JsonSerializer.Serialize(dto));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var replay = await ReadReceiptAsync<DefectDto>(
            connection, transaction, request.IdempotencyKey, "create-defect", fingerprint, cancellationToken).ConfigureAwait(false);
        if (replay.IsFailure)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceDefectSnapshot>>(replay.Error!);
        }

        if (replay.Value.Value is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Replayed(replay.Value.Value.ToModel());
        }

        await using (var command = new NpgsqlCommand(
            $"""
            INSERT INTO {MaintenanceMigrations.Schema}.defect
                (defect_id, asset_id, scope_id, summary, state, version, created_at, updated_at)
            VALUES (@id, @asset, @scope, @summary, @state, 1, @now, @now);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", dto.DefectId);
            command.Parameters.AddWithValue("asset", dto.AssetId);
            command.Parameters.AddWithValue("scope", dto.ScopeId);
            command.Parameters.AddWithValue("summary", dto.Summary);
            command.Parameters.AddWithValue("state", checked((short)dto.State));
            command.Parameters.AddWithValue("now", now);
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Failure<MaintenanceWorkCommandResult<MaintenanceDefectSnapshot>>(
                    "maintenance.defect_conflict", "Maintenance defect identity already exists.");
            }
        }

        await WriteReceiptAndAuditAsync(
            connection, transaction, request.IdempotencyKey, "create-defect", fingerprint, "defect", dto,
            authorization, dto.ScopeId, dto.DefectId, dto.Version, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Applied(dto.ToModel());
    }

    public async Task<Result<MaintenanceWorkCommandResult<MaintenanceDefectSnapshot>>> ConfirmDefectAsync(
        AuthorizedMutation authorization,
        ConfirmMaintenanceDefect request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fingerprint = Fingerprint(JsonSerializer.Serialize(new
        {
            DefectId = request.DefectId.Value,
            ExpectedVersion = request.ExpectedVersion.Value,
        }));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var replay = await ReadReceiptAsync<DefectDto>(
            connection, transaction, request.IdempotencyKey, "confirm-defect", fingerprint, cancellationToken).ConfigureAwait(false);
        if (replay.IsFailure)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceDefectSnapshot>>(replay.Error!);
        }

        if (replay.Value.Value is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Replayed(replay.Value.Value.ToModel());
        }

        var current = await ReadDefectDtoAsync(connection, transaction, request.DefectId, true, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return Failure<MaintenanceWorkCommandResult<MaintenanceDefectSnapshot>>(
                "maintenance.defect_not_found", "Maintenance defect was not found.");
        }

        var contract = ValidateTransition(
            authorization, MaintenanceWorkPermissions.Manage(FacilityScopeId.From(current.ScopeId)),
            request.ExpectedVersion, current.Version, request.IdempotencyKey);
        if (contract.IsFailure || current.State != (int)MaintenanceDefectState.Reported)
        {
            return contract.IsFailure
                ? Result.Failure<MaintenanceWorkCommandResult<MaintenanceDefectSnapshot>>(contract.Error!)
                : Failure<MaintenanceWorkCommandResult<MaintenanceDefectSnapshot>>(
                    "maintenance.defect_state", "Only a reported defect can be confirmed.");
        }

        var now = UtcNow();
        var next = current with
        {
            State = (int)MaintenanceDefectState.Confirmed,
            Version = request.ExpectedVersion.Next().Value,
            UpdatedAt = now,
        };
        await UpdateDefectAsync(connection, transaction, next, cancellationToken).ConfigureAwait(false);
        await WriteReceiptAndAuditAsync(
            connection, transaction, request.IdempotencyKey, "confirm-defect", fingerprint, "defect", next,
            authorization, next.ScopeId, next.DefectId, next.Version, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Applied(next.ToModel());
    }

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> CreateWorkOrderAsync(
        AuthorizedMutation authorization,
        CreateWorkOrderFromRequest request,
        CancellationToken cancellationToken = default) =>
        CreateWorkOrderCoreAsync(
            authorization, request.WorkOrderId, MaintenanceWorkSourceKind.Request, request.RequestId.Value,
            request.Summary, request.AssignedPersonId, request.Safety, request.Checklist,
            request.ExpectedSourceVersion, request.IdempotencyKey, cancellationToken);

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> CreateWorkOrderAsync(
        AuthorizedMutation authorization,
        CreateWorkOrderFromDefect request,
        CancellationToken cancellationToken = default) =>
        CreateWorkOrderCoreAsync(
            authorization, request.WorkOrderId, MaintenanceWorkSourceKind.Defect, request.DefectId.Value,
            request.Summary, request.AssignedPersonId, request.Safety, request.Checklist,
            request.ExpectedSourceVersion, request.IdempotencyKey, cancellationToken);

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> StartWorkOrderAsync(
        AuthorizedMutation authorization, PersonId actor, TransitionMaintenanceWorkOrder request,
        CancellationToken cancellationToken = default) =>
        TransitionWorkOrderAsync(
            authorization, actor, request, "start-work-order", MaintenanceWorkOrderState.Assigned,
            MaintenanceWorkOrderState.InProgress, requireChecklist: false, cancellationToken);

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> CompleteWorkOrderAsync(
        AuthorizedMutation authorization, PersonId actor, TransitionMaintenanceWorkOrder request,
        CancellationToken cancellationToken = default) =>
        TransitionWorkOrderAsync(
            authorization, actor, request, "complete-work-order", MaintenanceWorkOrderState.InProgress,
            MaintenanceWorkOrderState.Completed, requireChecklist: false, cancellationToken);

    public Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> AcceptWorkOrderAsync(
        AuthorizedMutation authorization, TransitionMaintenanceWorkOrder request,
        CancellationToken cancellationToken = default) =>
        TransitionWorkOrderAsync(
            authorization, null, request, "accept-work-order", MaintenanceWorkOrderState.Completed,
            MaintenanceWorkOrderState.Accepted, requireChecklist: true, cancellationToken);

    public async Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> CompleteChecklistItemAsync(
        AuthorizedMutation authorization,
        PersonId actor,
        CompleteWorkOrderChecklistItem request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fingerprint = Fingerprint(JsonSerializer.Serialize(new
        {
            WorkOrderId = request.WorkOrderId.Value,
            ItemId = request.ItemId.Value,
            Actor = actor.Value,
            ExpectedVersion = request.ExpectedVersion.Value,
        }));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var replay = await ReadReceiptAsync<WorkOrderDto>(
            connection, transaction, request.IdempotencyKey, "complete-checklist", fingerprint, cancellationToken).ConfigureAwait(false);
        if (replay.IsFailure)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(replay.Error!);
        }

        if (replay.Value.Value is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Replayed(replay.Value.Value.ToModel());
        }

        var current = await ReadWorkOrderDtoAsync(connection, transaction, request.WorkOrderId, true, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(
                "maintenance.work_order_not_found", "Work order was not found.");
        }

        var contract = ValidateTransition(
            authorization, MaintenanceWorkPermissions.Execute(FacilityScopeId.From(current.ScopeId)),
            request.ExpectedVersion, current.Version, request.IdempotencyKey);
        var item = current.Checklist.SingleOrDefault(value => value.ItemId == request.ItemId.Value);
        if (contract.IsFailure || current.AssignedPersonId != actor.Value ||
            current.State == (int)MaintenanceWorkOrderState.Accepted || item is null || item.CompletedAt is not null)
        {
            if (contract.IsFailure)
            {
                return Result.Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(contract.Error!);
            }

            return Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(
                current.AssignedPersonId != actor.Value ? "permission.denied" : "maintenance.checklist_state",
                current.AssignedPersonId != actor.Value
                    ? "Only the assigned person may complete checklist items."
                    : "Checklist item cannot be completed in its current state.");
        }

        var now = UtcNow();
        var completedItem = item with { CompletedAt = now, CompletedBy = actor.Value };
        await using (var command = new NpgsqlCommand(
            $"""
            UPDATE {MaintenanceMigrations.Schema}.work_order_checklist
            SET completed_at = @now, completed_by = @person WHERE checklist_item_id = @item;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("person", actor.Value);
            command.Parameters.AddWithValue("item", request.ItemId.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var next = current with
        {
            Version = request.ExpectedVersion.Next().Value,
            UpdatedAt = now,
            Checklist = current.Checklist.Select(value => value.ItemId == item.ItemId ? completedItem : value).ToList(),
        };
        await UpdateWorkOrderAsync(connection, transaction, next, cancellationToken).ConfigureAwait(false);
        await WriteReceiptAndAuditAsync(
            connection, transaction, request.IdempotencyKey, "complete-checklist", fingerprint, "work-order", next,
            authorization, next.ScopeId, next.WorkOrderId, next.Version, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Applied(next.ToModel());
    }

    public async Task<MaintenanceRequestSnapshot?> ReadRequestAsync(
        MaintenanceRequestId requestId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var value = await ReadRequestDtoAsync(connection, transaction, requestId, false, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return value?.ToModel();
    }

    public async Task<MaintenanceDefectSnapshot?> ReadDefectAsync(
        MaintenanceDefectId defectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var value = await ReadDefectDtoAsync(connection, transaction, defectId, false, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return value?.ToModel();
    }

    public async Task<MaintenanceWorkOrderSnapshot?> ReadWorkOrderAsync(
        MaintenanceWorkOrderId workOrderId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var value = await ReadWorkOrderDtoAsync(connection, transaction, workOrderId, false, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return value?.ToModel();
    }

    public async Task<long> CountAuditAsync(Guid entityId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {MaintenanceMigrations.Schema}.work_mutation_audit WHERE entity_id = @id;",
            connection, transaction);
        command.Parameters.AddWithValue("id", entityId);
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    private async Task<Result<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>> CreateRequestCoreAsync(
        AuthorizedMutation authorization,
        MaintenanceAssetSnapshot asset,
        MaintenanceRequestId requestId,
        string summary,
        string idempotencyKey,
        EventSourceDto? eventSource,
        CancellationToken cancellationToken)
    {
        var validation = ValidateCreate(
            authorization, MaintenanceWorkPermissions.Manage(asset.ScopeId), summary, idempotencyKey);
        if (validation.IsFailure)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>(validation.Error!);
        }

        var now = UtcNow();
        var dto = new RequestDto(
            requestId.Value, asset.AssetId.Value, asset.ScopeId.Value, summary.Trim(),
            (int)MaintenanceRequestState.Submitted, StateVersion.Initial.Value, eventSource, now, now);
        var fingerprint = Fingerprint(JsonSerializer.Serialize(dto));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var replay = await ReadReceiptAsync<RequestDto>(
            connection, transaction, idempotencyKey, "create-request", fingerprint, cancellationToken).ConfigureAwait(false);
        if (replay.IsFailure)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>(replay.Error!);
        }

        if (replay.Value.Value is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Replayed(replay.Value.Value.ToModel());
        }

        await using (var command = new NpgsqlCommand(
            $"""
            INSERT INTO {MaintenanceMigrations.Schema}.maintenance_request
                (request_id, asset_id, scope_id, summary, state, version, event_id, occurrence_id,
                 source_runtime_scope_id, point_id, source_route, source_permissions, created_at, updated_at)
            VALUES (@id, @asset, @scope, @summary, @state, 1, @event, @occurrence,
                    @runtime_scope, @point, @route, @permissions, @now, @now);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", dto.RequestId);
            command.Parameters.AddWithValue("asset", dto.AssetId);
            command.Parameters.AddWithValue("scope", dto.ScopeId);
            command.Parameters.AddWithValue("summary", dto.Summary);
            command.Parameters.AddWithValue("state", checked((short)dto.State));
            AddNullableUuid(command, "event", eventSource?.EventId);
            AddNullableUuid(command, "occurrence", eventSource?.OccurrenceId);
            AddNullableUuid(command, "runtime_scope", eventSource?.RuntimeScopeId);
            AddNullableUuid(command, "point", eventSource?.PointId);
            command.Parameters.Add(new NpgsqlParameter("route", NpgsqlDbType.Text)
            {
                Value = eventSource?.Route ?? (object)DBNull.Value,
            });
            command.Parameters.Add(new NpgsqlParameter("permissions", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = eventSource?.RequiredPermissions ?? (object)DBNull.Value,
            });
            command.Parameters.AddWithValue("now", now);
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Failure<MaintenanceWorkCommandResult<MaintenanceRequestSnapshot>>(
                    "maintenance.request_conflict", "Maintenance request identity or source event already exists.");
            }
        }

        await WriteReceiptAndAuditAsync(
            connection, transaction, idempotencyKey, "create-request", fingerprint, "request", dto,
            authorization, dto.ScopeId, dto.RequestId, dto.Version, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Applied(dto.ToModel());
    }

    private async Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> CreateWorkOrderCoreAsync(
        AuthorizedMutation authorization,
        MaintenanceWorkOrderId workOrderId,
        MaintenanceWorkSourceKind sourceKind,
        Guid sourceId,
        string summary,
        PersonId assignedPersonId,
        WorkOrderSafetyFields safety,
        IReadOnlyCollection<NewWorkOrderChecklistItem> checklist,
        StateVersion expectedSourceVersion,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(safety);
        ArgumentNullException.ThrowIfNull(checklist);
        if (safety.AcknowledgedAt is not null || safety.Instructions?.Length > 1000 ||
            checklist.Select(value => value.ItemId).Distinct().Count() != checklist.Count ||
            checklist.Any(value => string.IsNullOrWhiteSpace(value.Description) || value.Description.Length > 500))
        {
            return Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(
                "maintenance.work_order_invalid", "Work order safety or checklist is invalid.");
        }

        var action = sourceKind == MaintenanceWorkSourceKind.Request
            ? "create-work-order-request"
            : "create-work-order-defect";
        var fingerprint = Fingerprint(JsonSerializer.Serialize(new
        {
            WorkOrderId = workOrderId.Value, SourceKind = (int)sourceKind, SourceId = sourceId,
            Summary = summary.Trim(), AssignedPerson = assignedPersonId.Value, Safety = safety,
            Checklist = checklist.OrderBy(value => value.ItemId.Value), ExpectedVersion = expectedSourceVersion.Value,
        }));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var replay = await ReadReceiptAsync<WorkOrderDto>(
            connection, transaction, idempotencyKey, action, fingerprint, cancellationToken).ConfigureAwait(false);
        if (replay.IsFailure)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(replay.Error!);
        }

        if (replay.Value.Value is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Replayed(replay.Value.Value.ToModel());
        }

        var now = UtcNow();
        var source = await ReadAndConvertSourceAsync(
            connection, transaction, sourceKind, sourceId, expectedSourceVersion, authorization,
            now, cancellationToken).ConfigureAwait(false);
        if (source.IsFailure)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(source.Error!);
        }

        var createValidation = ValidateCreate(
            authorization, MaintenanceWorkPermissions.Manage(FacilityScopeId.From(source.Value.ScopeId)),
            summary, idempotencyKey);
        if (createValidation.IsFailure)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(createValidation.Error!);
        }

        var dto = new WorkOrderDto(
            workOrderId.Value, source.Value.AssetId, source.Value.ScopeId, (int)sourceKind, sourceId,
            summary.Trim(), assignedPersonId.Value, (int)MaintenanceWorkOrderState.Assigned,
            safety.PermitRequired, safety.IsolationRequired, safety.Instructions?.Trim(), null,
            StateVersion.Initial.Value,
            checklist.OrderBy(value => value.ItemId.Value)
                .Select(value => new ChecklistDto(value.ItemId.Value, value.Description.Trim(), value.Mandatory, null, null))
                .ToList(), now, now);
        await InsertWorkOrderAsync(connection, transaction, dto, cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync(
            connection, transaction, authorization, dto.ScopeId,
            sourceKind == MaintenanceWorkSourceKind.Request ? "request" : "defect", sourceId,
            "convert-to-work-order", expectedSourceVersion.Next().Value, now, cancellationToken).ConfigureAwait(false);
        await WriteReceiptAndAuditAsync(
            connection, transaction, idempotencyKey, action, fingerprint, "work-order", dto,
            authorization, dto.ScopeId, dto.WorkOrderId, dto.Version, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Applied(dto.ToModel());
    }

    private async Task<Result<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>> TransitionWorkOrderAsync(
        AuthorizedMutation authorization,
        PersonId? actor,
        TransitionMaintenanceWorkOrder request,
        string action,
        MaintenanceWorkOrderState requiredState,
        MaintenanceWorkOrderState nextState,
        bool requireChecklist,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fingerprint = Fingerprint(JsonSerializer.Serialize(new
        {
            WorkOrderId = request.WorkOrderId.Value,
            Actor = actor?.Value,
            ExpectedVersion = request.ExpectedVersion.Value,
            request.SafetyAcknowledged,
        }));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var replay = await ReadReceiptAsync<WorkOrderDto>(
            connection, transaction, request.IdempotencyKey, action, fingerprint, cancellationToken).ConfigureAwait(false);
        if (replay.IsFailure)
        {
            return Result.Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(replay.Error!);
        }

        if (replay.Value.Value is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Replayed(replay.Value.Value.ToModel());
        }

        var current = await ReadWorkOrderDtoAsync(connection, transaction, request.WorkOrderId, true, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(
                "maintenance.work_order_not_found", "Work order was not found.");
        }

        var requiredPermission = nextState == MaintenanceWorkOrderState.Accepted
            ? MaintenanceWorkPermissions.Accept(FacilityScopeId.From(current.ScopeId))
            : MaintenanceWorkPermissions.Execute(FacilityScopeId.From(current.ScopeId));
        var contract = ValidateTransition(
            authorization, requiredPermission, request.ExpectedVersion, current.Version, request.IdempotencyKey);
        if (contract.IsFailure || current.State != (int)requiredState ||
            actor is not null && current.AssignedPersonId != actor.Value.Value)
        {
            if (contract.IsFailure)
            {
                return Result.Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(contract.Error!);
            }

            return Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(
                actor is not null && current.AssignedPersonId != actor.Value.Value
                    ? "permission.denied"
                    : "maintenance.work_order_state",
                actor is not null && current.AssignedPersonId != actor.Value.Value
                    ? "Only the assigned person may execute the work order."
                    : "Work order transition is invalid for the current state.");
        }

        if (nextState == MaintenanceWorkOrderState.InProgress &&
            (current.PermitRequired || current.IsolationRequired || !string.IsNullOrWhiteSpace(current.SafetyInstructions)) &&
            !request.SafetyAcknowledged)
        {
            return Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(
                "maintenance.safety_acknowledgement", "Safety requirements must be acknowledged before work starts.");
        }

        if (requireChecklist && current.Checklist.Any(value => value.Mandatory && value.CompletedAt is null))
        {
            return Failure<MaintenanceWorkCommandResult<MaintenanceWorkOrderSnapshot>>(
                "maintenance.checklist_incomplete", "Mandatory checklist items block work order acceptance.");
        }

        var now = UtcNow();
        var next = current with
        {
            State = (int)nextState,
            SafetyAcknowledgedAt = nextState == MaintenanceWorkOrderState.InProgress && request.SafetyAcknowledged
                ? now
                : current.SafetyAcknowledgedAt,
            Version = request.ExpectedVersion.Next().Value,
            UpdatedAt = now,
        };
        await UpdateWorkOrderAsync(connection, transaction, next, cancellationToken).ConfigureAwait(false);
        await WriteReceiptAndAuditAsync(
            connection, transaction, request.IdempotencyKey, action, fingerprint, "work-order", next,
            authorization, next.ScopeId, next.WorkOrderId, next.Version, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Applied(next.ToModel());
    }

    private static Result ValidateCreate(
        AuthorizedMutation authorization, PermissionCode permission, string summary, string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        return authorization.Permission != permission
            ? Failure("maintenance.permission_contract", "Authorization does not match the work scope.")
            : string.IsNullOrWhiteSpace(summary) || summary.Trim().Length > 500 ||
              string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200
                ? Failure("maintenance.work_command_invalid", "Maintenance work command is invalid.")
                : Result.Success();
    }

    private static Result ValidateTransition(
        AuthorizedMutation authorization, PermissionCode permission,
        StateVersion expectedVersion, ulong currentVersion, string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (authorization.Permission != permission)
        {
            return Failure("maintenance.permission_contract", "Authorization does not match the work transition.");
        }

        return !expectedVersion.IsDefined || expectedVersion.Value != currentVersion ||
               string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200
            ? Failure("maintenance.work_version", "Maintenance work version is stale or invalid.")
            : Result.Success();
    }

    private static async Task<Result<SourceConversion>> ReadAndConvertSourceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MaintenanceWorkSourceKind sourceKind,
        Guid sourceId,
        StateVersion expectedVersion,
        AuthorizedMutation authorization,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (sourceKind == MaintenanceWorkSourceKind.Request)
        {
            var current = await ReadRequestDtoAsync(
                connection, transaction, MaintenanceRequestId.From(sourceId), true, cancellationToken).ConfigureAwait(false);
            if (current is null || current.Version != expectedVersion.Value ||
                current.State != (int)MaintenanceRequestState.Approved)
            {
                return Failure<SourceConversion>(
                    current is null ? "maintenance.request_not_found" : "maintenance.request_state",
                    current is null ? "Maintenance request was not found." : "Request is not approved at the expected version.");
            }

            if (authorization.Permission != MaintenanceWorkPermissions.Manage(FacilityScopeId.From(current.ScopeId)))
            {
                return Failure<SourceConversion>("maintenance.permission_contract", "Authorization does not match the source scope.");
            }

            var next = current with
            {
                State = (int)MaintenanceRequestState.Converted,
                Version = expectedVersion.Next().Value,
                UpdatedAt = now,
            };
            await UpdateRequestAsync(connection, transaction, next, cancellationToken).ConfigureAwait(false);
            return Result.Success(new SourceConversion(current.AssetId, current.ScopeId));
        }

        var defect = await ReadDefectDtoAsync(
            connection, transaction, MaintenanceDefectId.From(sourceId), true, cancellationToken).ConfigureAwait(false);
        if (defect is null || defect.Version != expectedVersion.Value ||
            defect.State != (int)MaintenanceDefectState.Confirmed)
        {
            return Failure<SourceConversion>(
                defect is null ? "maintenance.defect_not_found" : "maintenance.defect_state",
                defect is null ? "Maintenance defect was not found." : "Defect is not confirmed at the expected version.");
        }

        if (authorization.Permission != MaintenanceWorkPermissions.Manage(FacilityScopeId.From(defect.ScopeId)))
        {
            return Failure<SourceConversion>("maintenance.permission_contract", "Authorization does not match the source scope.");
        }

        var converted = defect with
        {
            State = (int)MaintenanceDefectState.Converted,
            Version = expectedVersion.Next().Value,
            UpdatedAt = now,
        };
        await UpdateDefectAsync(connection, transaction, converted, cancellationToken).ConfigureAwait(false);
        return Result.Success(new SourceConversion(defect.AssetId, defect.ScopeId));
    }

    private static async Task<Result<Receipt<T>>> ReadReceiptAsync<T>(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string key, string action, string fingerprint, CancellationToken cancellationToken)
        where T : class
    {
        await using var command = new NpgsqlCommand(
            $"SELECT action, fingerprint, result_snapshot::text FROM {MaintenanceMigrations.Schema}.work_command_receipt WHERE idempotency_key = @key;",
            connection, transaction);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Result.Success(new Receipt<T>(null));
        }

        if (!string.Equals(reader.GetString(0), action, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), fingerprint, StringComparison.Ordinal))
        {
            return Failure<Receipt<T>>("maintenance.idempotency_conflict", "Idempotency key was used for another work command.");
        }

        return Result.Success(new Receipt<T>(JsonSerializer.Deserialize<T>(reader.GetString(2))!));
    }

    private static async Task<RequestDto?> ReadRequestDtoAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, MaintenanceRequestId requestId,
        bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT request_id, asset_id, scope_id, summary, state, version, event_id, occurrence_id,
                   source_runtime_scope_id, point_id, source_route, source_permissions, created_at, updated_at
            FROM {MaintenanceMigrations.Schema}.maintenance_request
            WHERE request_id = @id{(forUpdate ? " FOR UPDATE" : string.Empty)};
            """, connection, transaction);
        command.Parameters.AddWithValue("id", requestId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        EventSourceDto? source = null;
        if (!reader.IsDBNull(6))
        {
            source = new EventSourceDto(
                reader.GetGuid(6), reader.GetGuid(7), reader.GetGuid(8), reader.GetGuid(9), reader.GetString(10),
                reader.GetFieldValue<string[]>(11));
        }

        return new RequestDto(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetInt16(4),
            checked((ulong)reader.GetInt64(5)), source,
            reader.GetFieldValue<DateTimeOffset>(12), reader.GetFieldValue<DateTimeOffset>(13));
    }

    private static async Task<DefectDto?> ReadDefectDtoAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, MaintenanceDefectId defectId,
        bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT defect_id, asset_id, scope_id, summary, state, version, created_at, updated_at
            FROM {MaintenanceMigrations.Schema}.defect
            WHERE defect_id = @id{(forUpdate ? " FOR UPDATE" : string.Empty)};
            """, connection, transaction);
        command.Parameters.AddWithValue("id", defectId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new DefectDto(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetInt16(4),
                checked((ulong)reader.GetInt64(5)), reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7))
            : null;
    }

    private static async Task<WorkOrderDto?> ReadWorkOrderDtoAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, MaintenanceWorkOrderId workOrderId,
        bool forUpdate, CancellationToken cancellationToken)
    {
        WorkOrderDto? result;
        await using (var command = new NpgsqlCommand(
            $"""
            SELECT work_order_id, asset_id, scope_id, source_kind, source_id, summary, assigned_person_id,
                   state, permit_required, isolation_required, safety_instructions, safety_acknowledged_at,
                   version, created_at, updated_at
            FROM {MaintenanceMigrations.Schema}.work_order
            WHERE work_order_id = @id{(forUpdate ? " FOR UPDATE" : string.Empty)};
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", workOrderId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            result = new WorkOrderDto(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt16(3), reader.GetGuid(4),
                reader.GetString(5), reader.GetGuid(6), reader.GetInt16(7), reader.GetBoolean(8), reader.GetBoolean(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                checked((ulong)reader.GetInt64(12)), [],
                reader.GetFieldValue<DateTimeOffset>(13), reader.GetFieldValue<DateTimeOffset>(14));
        }

        await using (var command = new NpgsqlCommand(
            $"""
            SELECT checklist_item_id, description, mandatory, completed_at, completed_by
            FROM {MaintenanceMigrations.Schema}.work_order_checklist
            WHERE work_order_id = @id ORDER BY checklist_item_id;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", workOrderId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Checklist.Add(new ChecklistDto(
                    reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4)));
            }
        }

        return result;
    }

    private static async Task InsertWorkOrderAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, WorkOrderDto value,
        CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand(
            $"""
            INSERT INTO {MaintenanceMigrations.Schema}.work_order
                (work_order_id, asset_id, scope_id, source_kind, source_id, summary, assigned_person_id,
                 state, permit_required, isolation_required, safety_instructions, safety_acknowledged_at,
                 version, created_at, updated_at)
            VALUES (@id, @asset, @scope, @kind, @source, @summary, @assigned, @state,
                    @permit, @isolation, @instructions, NULL, 1, @now, @now);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", value.WorkOrderId);
            command.Parameters.AddWithValue("asset", value.AssetId);
            command.Parameters.AddWithValue("scope", value.ScopeId);
            command.Parameters.AddWithValue("kind", checked((short)value.SourceKind));
            command.Parameters.AddWithValue("source", value.SourceId);
            command.Parameters.AddWithValue("summary", value.Summary);
            command.Parameters.AddWithValue("assigned", value.AssignedPersonId);
            command.Parameters.AddWithValue("state", checked((short)value.State));
            command.Parameters.AddWithValue("permit", value.PermitRequired);
            command.Parameters.AddWithValue("isolation", value.IsolationRequired);
            command.Parameters.Add(new NpgsqlParameter("instructions", NpgsqlDbType.Text)
            {
                Value = value.SafetyInstructions ?? (object)DBNull.Value,
            });
            command.Parameters.AddWithValue("now", value.CreatedAt);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var item in value.Checklist)
        {
            await using var command = new NpgsqlCommand(
                $"""
                INSERT INTO {MaintenanceMigrations.Schema}.work_order_checklist
                    (checklist_item_id, work_order_id, description, mandatory, completed_at, completed_by)
                VALUES (@item, @work_order, @description, @mandatory, NULL, NULL);
                """, connection, transaction);
            command.Parameters.AddWithValue("item", item.ItemId);
            command.Parameters.AddWithValue("work_order", value.WorkOrderId);
            command.Parameters.AddWithValue("description", item.Description);
            command.Parameters.AddWithValue("mandatory", item.Mandatory);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpdateRequestAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RequestDto value,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"UPDATE {MaintenanceMigrations.Schema}.maintenance_request SET state = @state, version = @version, updated_at = @updated WHERE request_id = @id;",
            connection, transaction);
        command.Parameters.AddWithValue("id", value.RequestId);
        command.Parameters.AddWithValue("state", checked((short)value.State));
        command.Parameters.AddWithValue("version", checked((long)value.Version));
        command.Parameters.AddWithValue("updated", value.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateDefectAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DefectDto value,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"UPDATE {MaintenanceMigrations.Schema}.defect SET state = @state, version = @version, updated_at = @updated WHERE defect_id = @id;",
            connection, transaction);
        command.Parameters.AddWithValue("id", value.DefectId);
        command.Parameters.AddWithValue("state", checked((short)value.State));
        command.Parameters.AddWithValue("version", checked((long)value.Version));
        command.Parameters.AddWithValue("updated", value.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateWorkOrderAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, WorkOrderDto value,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {MaintenanceMigrations.Schema}.work_order
            SET state = @state, safety_acknowledged_at = @acknowledged, version = @version, updated_at = @updated
            WHERE work_order_id = @id;
            """, connection, transaction);
        command.Parameters.AddWithValue("id", value.WorkOrderId);
        command.Parameters.AddWithValue("state", checked((short)value.State));
        command.Parameters.Add(new NpgsqlParameter("acknowledged", NpgsqlDbType.TimestampTz)
        {
            Value = value.SafetyAcknowledgedAt is null ? DBNull.Value : value.SafetyAcknowledgedAt.Value,
        });
        command.Parameters.AddWithValue("version", checked((long)value.Version));
        command.Parameters.AddWithValue("updated", value.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteReceiptAndAuditAsync<T>(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string key, string action, string fingerprint, string resultKind, T result,
        AuthorizedMutation authorization, Guid scopeId, Guid entityId, ulong version, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand(
            $"""
            INSERT INTO {MaintenanceMigrations.Schema}.work_command_receipt
                (idempotency_key, action, fingerprint, result_kind, result_snapshot, applied_at)
            VALUES (@key, @action, @fingerprint, @kind, CAST(@result AS jsonb), @now);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("action", action);
            command.Parameters.AddWithValue("fingerprint", fingerprint);
            command.Parameters.AddWithValue("kind", resultKind);
            command.Parameters.AddWithValue("result", JsonSerializer.Serialize(result));
            command.Parameters.AddWithValue("now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteAuditAsync(
            connection, transaction, authorization, scopeId, resultKind, entityId, action, version, now, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        AuthorizedMutation authorization, Guid scopeId, string entityKind, Guid entityId,
        string action, ulong version, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {MaintenanceMigrations.Schema}.work_mutation_audit
                (audit_id, scope_id, entity_kind, entity_id, session_id, subject_id, action, resulting_version, changed_at)
            VALUES (@audit, @scope, @kind, @entity, @session, @subject, @action, @version, @now);
            """, connection, transaction);
        command.Parameters.AddWithValue("audit", Guid.NewGuid());
        command.Parameters.AddWithValue("scope", scopeId);
        command.Parameters.AddWithValue("kind", entityKind);
        command.Parameters.AddWithValue("entity", entityId);
        command.Parameters.AddWithValue("session", authorization.Session.Id.Value);
        command.Parameters.AddWithValue("subject", authorization.Session.SubjectId.Value);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("version", checked((long)version));
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddNullableUuid(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid)
        {
            Value = value is null ? DBNull.Value : value.Value,
        });

    private async Task SetRoleAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE {databaseRole};", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private DateTimeOffset UtcNow()
    {
        var now = clock.GetUtcNow();
        return now.Offset == TimeSpan.Zero
            ? now
            : throw new InvalidOperationException("Maintenance work requires a UTC wall clock.");
    }

    private static string Fingerprint(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static Result<MaintenanceWorkCommandResult<T>> Applied<T>(T value) =>
        Result.Success(new MaintenanceWorkCommandResult<T>(value, MaintenanceWorkCommandDisposition.Applied));

    private static Result<MaintenanceWorkCommandResult<T>> Replayed<T>(T value) =>
        Result.Success(new MaintenanceWorkCommandResult<T>(value, MaintenanceWorkCommandDisposition.Replay));

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));

    private sealed record Receipt<T>(T? Value) where T : class;
    private sealed record SourceConversion(Guid AssetId, Guid ScopeId);

    private sealed record EventSourceDto(
        Guid EventId, Guid OccurrenceId, Guid RuntimeScopeId, Guid PointId,
        string Route, string[] RequiredPermissions)
    {
        public MaintenanceEventSourceLink ToModel() => new(
            Dispatcher.Events.OperationalEventId.From(EventId), Dispatcher.Alarm.AlarmOccurrenceId.From(OccurrenceId),
            Dispatcher.Core.RuntimeScopeId.From(RuntimeScopeId), Dispatcher.Semantics.PointId.From(PointId), Route,
            RequiredPermissions.Select(PermissionCode.From).ToArray());
    }

    private sealed record RequestDto(
        Guid RequestId, Guid AssetId, Guid ScopeId, string Summary, int State, ulong Version,
        EventSourceDto? EventSource, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
    {
        public MaintenanceRequestSnapshot ToModel() => new(
            MaintenanceRequestId.From(RequestId), MaintenanceAssetId.From(AssetId), FacilityScopeId.From(ScopeId),
            Summary, (MaintenanceRequestState)State, StateVersion.From(Version), EventSource?.ToModel(), CreatedAt, UpdatedAt);
    }

    private sealed record DefectDto(
        Guid DefectId, Guid AssetId, Guid ScopeId, string Summary, int State, ulong Version,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
    {
        public MaintenanceDefectSnapshot ToModel() => new(
            MaintenanceDefectId.From(DefectId), MaintenanceAssetId.From(AssetId), FacilityScopeId.From(ScopeId),
            Summary, (MaintenanceDefectState)State, StateVersion.From(Version), CreatedAt, UpdatedAt);
    }

    private sealed record ChecklistDto(
        Guid ItemId, string Description, bool Mandatory, DateTimeOffset? CompletedAt, Guid? CompletedBy)
    {
        public WorkOrderChecklistItem ToModel() => new(
            WorkOrderChecklistItemId.From(ItemId), Description, Mandatory, CompletedAt,
            CompletedBy is null ? null : PersonId.From(CompletedBy.Value));
    }

    private sealed record WorkOrderDto(
        Guid WorkOrderId, Guid AssetId, Guid ScopeId, int SourceKind, Guid SourceId,
        string Summary, Guid AssignedPersonId, int State, bool PermitRequired, bool IsolationRequired,
        string? SafetyInstructions, DateTimeOffset? SafetyAcknowledgedAt, ulong Version,
        List<ChecklistDto> Checklist, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
    {
        public MaintenanceWorkOrderSnapshot ToModel() => new(
            MaintenanceWorkOrderId.From(WorkOrderId), MaintenanceAssetId.From(AssetId), FacilityScopeId.From(ScopeId),
            (MaintenanceWorkSourceKind)SourceKind, SourceId, Summary, PersonId.From(AssignedPersonId),
            (MaintenanceWorkOrderState)State,
            new WorkOrderSafetyFields(PermitRequired, IsolationRequired, SafetyInstructions, SafetyAcknowledgedAt),
            StateVersion.From(Version), Checklist.Select(value => value.ToModel()).ToArray(), CreatedAt, UpdatedAt);
    }
}
