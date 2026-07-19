using System.Text.Json;
using System.Text.RegularExpressions;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;
using NpgsqlTypes;

namespace Dispatcher.Equipment;

public sealed partial class EquipmentStagingStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;

    public EquipmentStagingStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        if (!RolePattern().IsMatch(databaseRole))
        {
            throw new ArgumentException("Invalid PostgreSQL role name.", nameof(databaseRole));
        }

        this.dataSource = dataSource;
        this.databaseRole = databaseRole;
        this.clock = clock;
    }

    public async Task<Result<StagingRowSnapshot>> ReserveAsync(
        AuthorizedAccess authorization,
        StagingRowInput row,
        StagingSecretProtector protector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(protector);
        var errors = EquipmentStagingTools.Validate(row);
        if (errors.Count > 0)
        {
            return Failure<StagingRowSnapshot>(errors[0].Code, errors[0].Message);
        }

        var fingerprint = StagingFingerprint.Compute(row);
        var formJson = JsonSerializer.Serialize(new
        {
            row.Form.Host,
            row.Form.Port,
            row.Form.ModbusUnitId,
            row.Form.SnmpVersion,
        });
        var protectedSecret = row.Form.Secret is null ? null : protector.Protect(row.Form.Secret);
        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand(
                         $"""
                         INSERT INTO {EquipmentMigrations.Schema}.staging_row
                             (row_id, request_fingerprint, scope_id, equipment_id, location_id,
                              code, name, protocol, form_data, protected_secret, state, version, created_at, updated_at)
                         VALUES
                             (@row_id, @fingerprint, @scope_id, @equipment_id, @location_id,
                              @code, @name, @protocol, @form_data, @protected_secret, 1, 1, @now, @now)
                         ON CONFLICT DO NOTHING;
                         """,
                         connection,
                         transaction))
        {
            insert.Parameters.AddWithValue("row_id", row.RowId);
            insert.Parameters.AddWithValue("fingerprint", fingerprint);
            insert.Parameters.AddWithValue("scope_id", row.ScopeId.Value);
            insert.Parameters.AddWithValue("equipment_id", row.EquipmentId.Value);
            insert.Parameters.AddWithValue("location_id", row.LocationId.Value);
            insert.Parameters.AddWithValue("code", row.Code.Trim());
            insert.Parameters.AddWithValue("name", row.Name.Trim());
            insert.Parameters.AddWithValue("protocol", Protocol(row.Form.Protocol));
            insert.Parameters.AddWithValue("form_data", NpgsqlDbType.Jsonb, formJson);
            insert.Parameters.AddWithValue("protected_secret", (object?)protectedSecret ?? DBNull.Value);
            insert.Parameters.AddWithValue("now", now);
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            var workItem = await ReadWorkItemAsync(connection, transaction, row.RowId, true, cancellationToken)
                .ConfigureAwait(false);
            if (workItem is null || workItem.RequestFingerprint != fingerprint)
            {
                return Failure<StagingRowSnapshot>(
                    "staging.idempotency_conflict",
                    "The row ID was already used for a different request.");
            }

            if (inserted)
            {
                await InsertAuditAsync(
                    connection,
                    transaction,
                    authorization,
                    workItem.Snapshot,
                    "staging.reserve",
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(workItem.Snapshot);
        }
    }

    public async Task<Result<StagingWorkItem>> ReadWorkItemAsync(
        FacilityScopeId scopeId,
        Guid rowId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var item = await ReadWorkItemAsync(connection, transaction, rowId, false, cancellationToken).ConfigureAwait(false);
        if (item is null || item.Snapshot.ScopeId != scopeId)
        {
            return Failure<StagingWorkItem>("staging.row_not_found", "Staging row was not found in this scope.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(item);
    }

    public async Task<Result<StagingRowSnapshot>> AdvanceAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        Guid rowId,
        StagingRowState expectedState,
        StagingRowState nextState,
        CancellationToken cancellationToken = default)
    {
        if ((int)nextState != (int)expectedState + 1)
        {
            throw new ArgumentException("Staging state must advance exactly once.", nameof(nextState));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var item = await ReadWorkItemAsync(connection, transaction, rowId, true, cancellationToken).ConfigureAwait(false);
        if (item is null || item.Snapshot.ScopeId != scopeId)
        {
            return Failure<StagingRowSnapshot>("staging.row_not_found", "Staging row was not found in this scope.");
        }

        if (item.Snapshot.State == nextState)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(item.Snapshot);
        }

        if (item.Snapshot.State != expectedState)
        {
            return Failure<StagingRowSnapshot>("staging.state_conflict", "Staging row state changed concurrently.");
        }

        var snapshot = item.Snapshot with { State = nextState, Version = item.Snapshot.Version + 1 };
        await using (var update = new NpgsqlCommand(
                         $"""
                         UPDATE {EquipmentMigrations.Schema}.staging_row
                         SET state = @state, version = @version, updated_at = @now
                         WHERE row_id = @row_id AND state = @expected_state AND version = @expected_version;
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("state", (short)nextState);
            update.Parameters.AddWithValue("version", snapshot.Version);
            update.Parameters.AddWithValue("now", clock.GetUtcNow());
            update.Parameters.AddWithValue("row_id", rowId);
            update.Parameters.AddWithValue("expected_state", (short)expectedState);
            update.Parameters.AddWithValue("expected_version", item.Snapshot.Version);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return Failure<StagingRowSnapshot>("staging.state_conflict", "Staging row state changed concurrently.");
            }
        }

        await InsertAuditAsync(
            connection,
            transaction,
            authorization,
            snapshot,
            nextState == StagingRowState.EquipmentAccepted
                ? "staging.equipment_accepted"
                : "staging.configuration_accepted",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(snapshot);
    }

    private static async Task<StagingWorkItem?> ReadWorkItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid rowId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT row_id, request_fingerprint, scope_id, equipment_id, location_id,
                   code, name, protocol, form_data::text, protected_secret, state, version
            FROM {EquipmentMigrations.Schema}.staging_row
            WHERE row_id = @row_id {(forUpdate ? "FOR UPDATE" : string.Empty)};
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("row_id", rowId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        using var form = JsonDocument.Parse(reader.GetString(8));
        var root = form.RootElement;
        var protocol = reader.GetString(7) == "modbus_tcp" ? EquipmentProtocol.ModbusTcp : EquipmentProtocol.Snmp;
        var snapshot = new StagingRowSnapshot(
            reader.GetGuid(0),
            EquipmentId.From(reader.GetGuid(3)),
            FacilityScopeId.From(reader.GetGuid(2)),
            LocationId.From(reader.GetGuid(4)),
            reader.GetString(5),
            reader.GetString(6),
            protocol,
            root.GetProperty("Host").GetString()!,
            root.GetProperty("Port").GetInt32(),
            NullableInt(root, "ModbusUnitId"),
            NullableString(root, "SnmpVersion"),
            !reader.IsDBNull(9),
            (StagingRowState)reader.GetInt16(10),
            reader.GetInt64(11));
        return new StagingWorkItem(
            snapshot,
            reader.GetString(1),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<byte[]>(9));
    }

    private async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuthorizedAccess authorization,
        StagingRowSnapshot row,
        string action,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {EquipmentMigrations.Schema}.staging_audit
                (audit_id, row_id, scope_id, equipment_id, session_id, subject_id,
                 permission, action, resulting_version, changed_at)
            VALUES
                (@audit_id, @row_id, @scope_id, @equipment_id, @session_id, @subject_id,
                 @permission, @action, @version, @changed_at);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("audit_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("row_id", row.RowId);
        command.Parameters.AddWithValue("scope_id", row.ScopeId.Value);
        command.Parameters.AddWithValue("equipment_id", row.EquipmentId.Value);
        command.Parameters.AddWithValue("session_id", authorization.Session.Id.Value);
        command.Parameters.AddWithValue("subject_id", authorization.Session.SubjectId.Value);
        command.Parameters.AddWithValue("permission", authorization.Permission.Value);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("version", row.Version);
        command.Parameters.AddWithValue("changed_at", clock.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SetRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE \"{databaseRole}\";", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int? NullableInt(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : root.GetProperty(name).GetInt32();

    private static string? NullableString(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : root.GetProperty(name).GetString();

    private static string Protocol(EquipmentProtocol protocol) =>
        protocol == EquipmentProtocol.ModbusTcp ? "modbus_tcp" : "snmp";

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
