using System.Text.RegularExpressions;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;

namespace Dispatcher.Facilities;

public sealed partial class FacilityStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;

    public FacilityStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock)
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

    public async Task<LocationSnapshot?> ReadLocationAsync(
        LocationId locationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var location = await ReadLocationAsync(connection, transaction, locationId, false, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return location;
    }

    public async Task<FacilityGraph> ReadGraphAsync(
        FacilityScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var locations = new List<LocationSnapshot>();
        await using (var command = new NpgsqlCommand(
                         $"""
                         SELECT location_id, scope_id, physical_parent_id, code, name, version
                         FROM {FacilityMigrations.Schema}.location
                         WHERE scope_id = @scope_id
                         ORDER BY code, location_id;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                locations.Add(MapLocation(reader));
            }
        }

        var relations = new List<FunctionalLocationRelation>();
        await using (var command = new NpgsqlCommand(
                         $"""
                         SELECT relation.source_location_id, relation.target_location_id
                         FROM {FacilityMigrations.Schema}.functional_relation relation
                         JOIN {FacilityMigrations.Schema}.location source
                           ON source.location_id = relation.source_location_id
                         WHERE source.scope_id = @scope_id
                         ORDER BY relation.source_location_id, relation.target_location_id;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                relations.Add(new FunctionalLocationRelation(
                    LocationId.From(reader.GetGuid(0)),
                    LocationId.From(reader.GetGuid(1))));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FacilityGraph(scopeId, locations, relations);
    }

    public async Task<Result<FacilityMutation>> CreateLocationAsync(
        AuthorizedAccess authorization,
        CreateLocation request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
        {
            return Failure("facility.location_invalid", "Location code and name are required.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await AcquireScopeLockAsync(connection, transaction, request.ScopeId, cancellationToken).ConfigureAwait(false);
        if (request.PhysicalParentId is not null)
        {
            if (request.PhysicalParentId.Value == request.LocationId)
            {
                return Failure("facility.physical_cycle", "Physical containment cannot contain a cycle.");
            }

            var parent = await ReadLocationAsync(
                connection,
                transaction,
                request.PhysicalParentId.Value,
                true,
                cancellationToken).ConfigureAwait(false);
            if (parent is null || parent.ScopeId != request.ScopeId)
            {
                return Failure("facility.parent_scope", "Physical parent must exist in the same scope.");
            }
        }

        await using (var command = new NpgsqlCommand(
                         $"""
                         INSERT INTO {FacilityMigrations.Schema}.location
                             (location_id, scope_id, physical_parent_id, code, name, version)
                         VALUES (@location_id, @scope_id, @parent_id, @code, @name, 1)
                         ON CONFLICT DO NOTHING
                         RETURNING location_id;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("location_id", request.LocationId.Value);
            command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            command.Parameters.AddWithValue("parent_id", (object?)request.PhysicalParentId?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("code", request.Code.Trim());
            command.Parameters.AddWithValue("name", request.Name.Trim());
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid)
            {
                return Failure("facility.identity_conflict", "Location identity or scoped code already exists.");
            }
        }

        await InsertAuditAsync(
            connection,
            transaction,
            authorization,
            request.LocationId,
            request.ScopeId,
            "location.create",
            1,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new FacilityMutation(request.LocationId, 1));
    }

    public async Task<Result<FacilityMutation>> MoveLocationAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        MoveLocation request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await AcquireScopeLockAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        var location = await ReadLocationAsync(connection, transaction, request.LocationId, true, cancellationToken)
            .ConfigureAwait(false);
        var validation = ValidateCurrent(location, scopeId, request.ExpectedVersion);
        if (validation is not null)
        {
            return Result.Failure<FacilityMutation>(validation);
        }

        if (request.PhysicalParentId is not null)
        {
            if (request.PhysicalParentId.Value == request.LocationId)
            {
                return Failure("facility.physical_cycle", "Physical containment cannot contain a cycle.");
            }

            var parent = await ReadLocationAsync(
                connection,
                transaction,
                request.PhysicalParentId.Value,
                true,
                cancellationToken).ConfigureAwait(false);
            if (parent is null || parent.ScopeId != scopeId)
            {
                return Failure("facility.parent_scope", "Physical parent must exist in the same scope.");
            }

            if (await IsDescendantAsync(
                connection,
                transaction,
                request.LocationId,
                request.PhysicalParentId.Value,
                cancellationToken).ConfigureAwait(false))
            {
                return Failure("facility.physical_cycle", "Physical containment cannot contain a cycle.");
            }
        }

        var nextVersion = checked(request.ExpectedVersion + 1);
        await using (var command = new NpgsqlCommand(
                         $"""
                         UPDATE {FacilityMigrations.Schema}.location
                         SET physical_parent_id = @parent_id, version = @next_version
                         WHERE location_id = @location_id AND scope_id = @scope_id AND version = @expected_version;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("parent_id", (object?)request.PhysicalParentId?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("next_version", nextVersion);
            command.Parameters.AddWithValue("location_id", request.LocationId.Value);
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("expected_version", request.ExpectedVersion);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return Failure("facility.version_conflict", "Location version changed concurrently.");
            }
        }

        await InsertAuditAsync(
            connection,
            transaction,
            authorization,
            request.LocationId,
            scopeId,
            "location.move",
            nextVersion,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new FacilityMutation(request.LocationId, nextVersion));
    }

    public async Task<Result<FacilityMutation>> AddFunctionalRelationAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        AddFunctionalLocationRelation request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceLocationId == request.TargetLocationId)
        {
            return Failure("facility.functional_self", "A functional relation cannot target itself.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await AcquireScopeLockAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        var source = await ReadLocationAsync(
            connection,
            transaction,
            request.SourceLocationId,
            true,
            cancellationToken).ConfigureAwait(false);
        var validation = ValidateCurrent(source, scopeId, request.ExpectedSourceVersion);
        if (validation is not null)
        {
            return Result.Failure<FacilityMutation>(validation);
        }

        var target = await ReadLocationAsync(
            connection,
            transaction,
            request.TargetLocationId,
            true,
            cancellationToken).ConfigureAwait(false);
        if (target is null || target.ScopeId != scopeId)
        {
            return Failure("facility.functional_scope", "Functional endpoints must belong to the same scope.");
        }

        await using (var insert = new NpgsqlCommand(
                         $"""
                         INSERT INTO {FacilityMigrations.Schema}.functional_relation
                             (source_location_id, target_location_id)
                         VALUES (@source_id, @target_id)
                         ON CONFLICT DO NOTHING
                         RETURNING source_location_id;
                         """,
                         connection,
                         transaction))
        {
            insert.Parameters.AddWithValue("source_id", request.SourceLocationId.Value);
            insert.Parameters.AddWithValue("target_id", request.TargetLocationId.Value);
            if (await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid)
            {
                return Failure("facility.functional_conflict", "The functional relation already exists.");
            }
        }

        var nextVersion = checked(request.ExpectedSourceVersion + 1);
        await using (var update = new NpgsqlCommand(
                         $"""
                         UPDATE {FacilityMigrations.Schema}.location
                         SET version = @next_version
                         WHERE location_id = @source_id AND version = @expected_version;
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("next_version", nextVersion);
            update.Parameters.AddWithValue("source_id", request.SourceLocationId.Value);
            update.Parameters.AddWithValue("expected_version", request.ExpectedSourceVersion);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return Failure("facility.version_conflict", "Location version changed concurrently.");
            }
        }

        await InsertAuditAsync(
            connection,
            transaction,
            authorization,
            request.SourceLocationId,
            scopeId,
            "functional_relation.add",
            nextVersion,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new FacilityMutation(request.SourceLocationId, nextVersion));
    }

    private static async Task<LocationSnapshot?> ReadLocationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LocationId locationId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var suffix = forUpdate ? " FOR UPDATE" : string.Empty;
        await using var command = new NpgsqlCommand(
            $"""
            SELECT location_id, scope_id, physical_parent_id, code, name, version
            FROM {FacilityMigrations.Schema}.location
            WHERE location_id = @location_id{suffix};
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("location_id", locationId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapLocation(reader)
            : null;
    }

    private static LocationSnapshot MapLocation(NpgsqlDataReader reader) => new(
        LocationId.From(reader.GetGuid(0)),
        FacilityScopeId.From(reader.GetGuid(1)),
        reader.IsDBNull(2) ? null : LocationId.From(reader.GetGuid(2)),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5));

    private static OperationError? ValidateCurrent(
        LocationSnapshot? location,
        FacilityScopeId scopeId,
        long expectedVersion)
    {
        if (location is null || location.ScopeId != scopeId)
        {
            return Error("facility.location_not_found", "The location does not exist in this scope.");
        }

        return location.Version != expectedVersion
            ? Error("facility.version_conflict", "Location version changed concurrently.")
            : null;
    }

    private static async Task<bool> IsDescendantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        LocationId locationId,
        LocationId proposedParentId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            WITH RECURSIVE descendants AS (
                SELECT location_id
                FROM {FacilityMigrations.Schema}.location
                WHERE physical_parent_id = @location_id
                UNION ALL
                SELECT child.location_id
                FROM {FacilityMigrations.Schema}.location child
                JOIN descendants parent ON child.physical_parent_id = parent.location_id
            )
            SELECT EXISTS (SELECT 1 FROM descendants WHERE location_id = @parent_id);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("location_id", locationId.Value);
        command.Parameters.AddWithValue("parent_id", proposedParentId.Value);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuthorizedAccess authorization,
        LocationId locationId,
        FacilityScopeId scopeId,
        string action,
        long version,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {FacilityMigrations.Schema}.mutation_audit
                (audit_id, location_id, scope_id, session_id, subject_id, permission, action, resulting_version, changed_at)
            VALUES
                (@audit_id, @location_id, @scope_id, @session_id, @subject_id, @permission, @action, @version, @changed_at);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("audit_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("location_id", locationId.Value);
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

    private static async Task AcquireScopeLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FacilityScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@scope_id, 0));",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Result<FacilityMutation> Failure(string code, string message) =>
        Result.Failure<FacilityMutation>(Error(code, message));

    private static OperationError Error(string code, string message) =>
        new(ErrorCode.From(code), message);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
