using System.Text.RegularExpressions;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;

namespace Dispatcher.Equipment;

public sealed partial class EquipmentStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;

    public EquipmentStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        ArgumentNullException.ThrowIfNull(clock);
        if (!RolePattern().IsMatch(databaseRole))
        {
            throw new ArgumentException("Invalid PostgreSQL role name.", nameof(databaseRole));
        }

        this.dataSource = dataSource;
        this.databaseRole = databaseRole;
        this.clock = clock;
    }

    public async Task<EquipmentRegistry> ReadRegistryAsync(
        FacilityScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var equipment = new List<EquipmentSnapshot>();
        await using (var command = new NpgsqlCommand(
                         $"""
                         SELECT equipment_id, scope_id, location_id, code, name, version
                         FROM {EquipmentMigrations.Schema}.equipment
                         WHERE scope_id = @scope_id
                         ORDER BY code, equipment_id;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                equipment.Add(MapEquipment(reader));
            }
        }

        var points = new List<PointDefinition>();
        await using (var command = new NpgsqlCommand(
                         $"""
                         SELECT point.point_id, point.equipment_id, point.code, point.name, point.unit_symbol, point.version
                         FROM {EquipmentMigrations.Schema}.point_definition point
                         JOIN {EquipmentMigrations.Schema}.equipment equipment
                           ON equipment.equipment_id = point.equipment_id
                         WHERE equipment.scope_id = @scope_id
                         ORDER BY point.equipment_id, point.code, point.point_id;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                points.Add(new PointDefinition(
                    PointId.From(reader.GetGuid(0)),
                    EquipmentId.From(reader.GetGuid(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    Unit.FromSymbol(reader.GetString(4)),
                    reader.GetInt64(5)));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new EquipmentRegistry(scopeId, equipment, points);
    }

    public async Task<Result<EquipmentMutation>> CreateEquipmentAsync(
        AuthorizedAccess authorization,
        CreateEquipment request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
        {
            return Failure("equipment.invalid", "Equipment code and name are required.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand(
                         $"""
                         INSERT INTO {EquipmentMigrations.Schema}.equipment
                             (equipment_id, scope_id, location_id, code, name, version)
                         VALUES (@equipment_id, @scope_id, @location_id, @code, @name, 1)
                         ON CONFLICT DO NOTHING
                         RETURNING equipment_id;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("equipment_id", request.EquipmentId.Value);
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            command.Parameters.AddWithValue("location_id", request.LocationId.Value);
            command.Parameters.AddWithValue("code", request.Code.Trim());
            command.Parameters.AddWithValue("name", request.Name.Trim());
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid)
            {
                return Failure("equipment.identity_conflict", "Equipment identity or scoped code already exists.");
            }
        }

        await InsertAuditAsync(
            connection,
            transaction,
            authorization,
            request.EquipmentId,
            null,
            request.ScopeId,
            "equipment.create",
            1,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new EquipmentMutation(request.EquipmentId, 1));
    }

    public async Task<Result<EquipmentMutation>> MoveEquipmentAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        MoveEquipment request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var equipment = await ReadEquipmentAsync(
            connection,
            transaction,
            request.EquipmentId,
            cancellationToken).ConfigureAwait(false);
        var validation = ValidateCurrent(equipment, scopeId, request.ExpectedVersion);
        if (validation is not null)
        {
            return Result.Failure<EquipmentMutation>(validation);
        }

        var nextVersion = checked(request.ExpectedVersion + 1);
        await using (var command = new NpgsqlCommand(
                         $"""
                         UPDATE {EquipmentMigrations.Schema}.equipment
                         SET location_id = @location_id, version = @next_version
                         WHERE equipment_id = @equipment_id AND scope_id = @scope_id AND version = @expected_version;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("location_id", request.LocationId.Value);
            command.Parameters.AddWithValue("next_version", nextVersion);
            command.Parameters.AddWithValue("equipment_id", request.EquipmentId.Value);
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("expected_version", request.ExpectedVersion);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return Failure("equipment.version_conflict", "Equipment version changed concurrently.");
            }
        }

        await InsertAuditAsync(
            connection,
            transaction,
            authorization,
            request.EquipmentId,
            null,
            scopeId,
            "equipment.move",
            nextVersion,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new EquipmentMutation(request.EquipmentId, nextVersion));
    }

    public async Task<Result<EquipmentMutation>> AddPointAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        AddPointDefinition request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
        {
            return Failure("equipment.point_invalid", "Point code and name are required.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var equipment = await ReadEquipmentAsync(
            connection,
            transaction,
            request.EquipmentId,
            cancellationToken).ConfigureAwait(false);
        var validation = ValidateCurrent(equipment, scopeId, request.ExpectedEquipmentVersion);
        if (validation is not null)
        {
            return Result.Failure<EquipmentMutation>(validation);
        }

        await using (var insert = new NpgsqlCommand(
                         $"""
                         INSERT INTO {EquipmentMigrations.Schema}.point_definition
                             (point_id, equipment_id, code, name, unit_symbol, version)
                         VALUES (@point_id, @equipment_id, @code, @name, @unit, 1)
                         ON CONFLICT DO NOTHING
                         RETURNING point_id;
                         """,
                         connection,
                         transaction))
        {
            insert.Parameters.AddWithValue("point_id", request.PointId.Value);
            insert.Parameters.AddWithValue("equipment_id", request.EquipmentId.Value);
            insert.Parameters.AddWithValue("code", request.Code.Trim());
            insert.Parameters.AddWithValue("name", request.Name.Trim());
            insert.Parameters.AddWithValue("unit", request.Unit.Symbol);
            if (await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid)
            {
                return Failure("equipment.point_conflict", "Point identity or equipment code already exists.");
            }
        }

        var nextVersion = checked(request.ExpectedEquipmentVersion + 1);
        await using (var update = new NpgsqlCommand(
                         $"""
                         UPDATE {EquipmentMigrations.Schema}.equipment
                         SET version = @next_version
                         WHERE equipment_id = @equipment_id AND version = @expected_version;
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("next_version", nextVersion);
            update.Parameters.AddWithValue("equipment_id", request.EquipmentId.Value);
            update.Parameters.AddWithValue("expected_version", request.ExpectedEquipmentVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return Failure("equipment.version_conflict", "Equipment version changed concurrently.");
            }
        }

        await InsertAuditAsync(
            connection,
            transaction,
            authorization,
            request.EquipmentId,
            request.PointId,
            scopeId,
            "point.create",
            nextVersion,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new EquipmentMutation(request.EquipmentId, nextVersion));
    }

    private static async Task<EquipmentSnapshot?> ReadEquipmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EquipmentId equipmentId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT equipment_id, scope_id, location_id, code, name, version
            FROM {EquipmentMigrations.Schema}.equipment
            WHERE equipment_id = @equipment_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("equipment_id", equipmentId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapEquipment(reader)
            : null;
    }

    private static EquipmentSnapshot MapEquipment(NpgsqlDataReader reader) => new(
        EquipmentId.From(reader.GetGuid(0)),
        FacilityScopeId.From(reader.GetGuid(1)),
        LocationId.From(reader.GetGuid(2)),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5));

    private static OperationError? ValidateCurrent(
        EquipmentSnapshot? equipment,
        FacilityScopeId scopeId,
        long expectedVersion)
    {
        if (equipment is null || equipment.ScopeId != scopeId)
        {
            return Error("equipment.not_found", "Equipment does not exist in this scope.");
        }

        return equipment.Version != expectedVersion
            ? Error("equipment.version_conflict", "Equipment version changed concurrently.")
            : null;
    }

    private async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuthorizedAccess authorization,
        EquipmentId equipmentId,
        PointId? pointId,
        FacilityScopeId scopeId,
        string action,
        long version,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {EquipmentMigrations.Schema}.mutation_audit
                (audit_id, equipment_id, point_id, scope_id, session_id, subject_id, permission, action, resulting_version, changed_at)
            VALUES
                (@audit_id, @equipment_id, @point_id, @scope_id, @session_id, @subject_id, @permission, @action, @version, @changed_at);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("audit_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("equipment_id", equipmentId.Value);
        command.Parameters.AddWithValue("point_id", (object?)pointId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("session_id", authorization.Session.Id.Value);
        command.Parameters.AddWithValue("subject_id", authorization.Session.SubjectId.Value);
        command.Parameters.AddWithValue("permission", authorization.Permission.Value);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("version", version);
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

    private static Result<EquipmentMutation> Failure(string code, string message) =>
        Result.Failure<EquipmentMutation>(Error(code, message));

    private static OperationError Error(string code, string message) =>
        new(ErrorCode.From(code), message);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
