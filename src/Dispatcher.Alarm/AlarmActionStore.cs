using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dispatcher.Core;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;

namespace Dispatcher.Alarm;

public sealed partial class AlarmActionStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;
    private readonly AlarmActionCommitHook? commitHook;

    public AlarmActionStore(
        NpgsqlDataSource dataSource,
        string databaseRole,
        IWallClock clock,
        AlarmActionCommitHook? commitHook = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        ArgumentNullException.ThrowIfNull(clock);
        if (!RolePattern().IsMatch(databaseRole))
        {
            throw new ArgumentException("Database role is not a valid PostgreSQL identifier.", nameof(databaseRole));
        }

        this.dataSource = dataSource;
        this.databaseRole = databaseRole;
        this.clock = clock;
        this.commitHook = commitHook;
    }

    public Task<Result<AlarmActionResult>> AcknowledgeAsync(
        AuthorizedMutation authorization,
        AcknowledgeAlarmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            authorization,
            request.ScopeId,
            request.PointId,
            request.OccurrenceId,
            request.ExpectedVersion,
            request.IdempotencyKey,
            request.Constraint,
            AlarmActionKind.Acknowledge,
            JsonSerializer.Serialize(new
            {
                OccurrenceId = request.OccurrenceId.Value,
                ExpectedVersion = request.ExpectedVersion.Value,
            }),
            occurrence => Acknowledge(authorization, occurrence, request),
            UpdateAcknowledgementAsync,
            cancellationToken);
    }

    public Task<Result<AlarmActionResult>> AssignAsync(
        AuthorizedMutation authorization,
        AssignAlarmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AssignedTo == Guid.Empty)
        {
            throw new ArgumentException("Assigned subject identity cannot be empty.", nameof(request));
        }

        return ExecuteAsync(
            authorization,
            request.ScopeId,
            request.PointId,
            request.OccurrenceId,
            request.ExpectedVersion,
            request.IdempotencyKey,
            request.Constraint,
            AlarmActionKind.Assign,
            JsonSerializer.Serialize(new
            {
                OccurrenceId = request.OccurrenceId.Value,
                request.AssignedTo,
                ExpectedVersion = request.ExpectedVersion.Value,
            }),
            occurrence => Assign(occurrence, request),
            UpdateAssignmentAsync,
            cancellationToken);
    }

    public Task<Result<AlarmActionResult>> ShelveAsync(
        AuthorizedMutation authorization,
        ShelveAlarmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);
        if (request.Reason.Length > 500 || request.ShelvedUntil.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Shelving requires a UTC expiry and a reason limited to 500 characters.", nameof(request));
        }

        return ExecuteAsync(
            authorization,
            request.ScopeId,
            request.PointId,
            request.OccurrenceId,
            request.ExpectedVersion,
            request.IdempotencyKey,
            request.Constraint,
            AlarmActionKind.Shelve,
            JsonSerializer.Serialize(new
            {
                OccurrenceId = request.OccurrenceId.Value,
                request.ShelvedUntil,
                Reason = request.Reason.Trim(),
                ExpectedVersion = request.ExpectedVersion.Value,
            }),
            occurrence => Shelve(occurrence, request),
            UpdateShelvingAsync,
            cancellationToken);
    }

    public async Task<long> CountAuditAsync(
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {AlarmMigrations.Schema}.mutation_audit WHERE scope_id = @scope_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    private async Task<Result<AlarmActionResult>> ExecuteAsync(
        AuthorizedMutation authorization,
        RuntimeScopeId scopeId,
        PointId pointId,
        AlarmOccurrenceId occurrenceId,
        StateVersion expectedVersion,
        string idempotencyKey,
        AlarmMaintenanceConstraintSnapshot constraint,
        AlarmActionKind action,
        string requestContent,
        Func<AlarmOccurrenceSnapshot, Result<AlarmOccurrenceSnapshot>> transform,
        Func<NpgsqlConnection, NpgsqlTransaction, AlarmOccurrenceSnapshot, StateVersion, CancellationToken, Task<int>> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(constraint);
        if (idempotencyKey.Length > 200 || !expectedVersion.IsDefined)
        {
            throw new ArgumentOutOfRangeException(nameof(idempotencyKey));
        }

        var requiredPermission = Permission(action);
        if (authorization.Permission != requiredPermission)
        {
            return Failure<AlarmActionResult>("alarm.permission_contract", "Authorization does not match Alarm action.");
        }

        if (constraint.OccurrenceId != occurrenceId)
        {
            return Failure<AlarmActionResult>("alarm.maintenance_constraint", "Maintenance constraint targets another occurrence.");
        }

        var now = clock.GetUtcNow();
        if (now.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Alarm actions require a UTC wall clock.");
        }

        var fingerprint = Hash(requestContent);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);

        var existing = await ReadActionAsync(
            connection,
            transaction,
            authorization.Session.SubjectId,
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Action != action || !string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return Failure<AlarmActionResult>(
                    "alarm.idempotency_conflict",
                    "The Alarm idempotency key was already used for another request.");
            }

            var replay = JsonSerializer.Deserialize<OccurrenceDto>(existing.SnapshotJson)
                ?? throw new InvalidOperationException("Stored Alarm action result is invalid.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(new AlarmActionResult(replay.ToModel(), AlarmActionDisposition.Replay));
        }

        var occurrence = await ReadOccurrenceAsync(
            connection,
            transaction,
            scopeId,
            occurrenceId,
            cancellationToken).ConfigureAwait(false);
        if (occurrence is null)
        {
            return Failure<AlarmActionResult>("alarm.occurrence_not_found", "Alarm occurrence was not found.");
        }

        if (occurrence.PointId != pointId)
        {
            return Failure<AlarmActionResult>("alarm.point_mismatch", "Alarm action point does not match occurrence.");
        }

        var transformed = transform(occurrence);
        if (transformed.IsFailure)
        {
            return Result.Failure<AlarmActionResult>(transformed.Error!);
        }

        if (await update(
                connection,
                transaction,
                transformed.Value,
                expectedVersion,
                cancellationToken).ConfigureAwait(false) != 1)
        {
            return Failure<AlarmActionResult>("alarm.expected_version", "Alarm facet version changed concurrently.");
        }

        var actionId = Guid.CreateVersion7();
        var snapshotJson = JsonSerializer.Serialize(OccurrenceDto.From(transformed.Value));
        var resultingVersion = FacetVersion(action, transformed.Value);
        await InsertActionAsync(
            connection,
            transaction,
            authorization,
            actionId,
            action,
            scopeId,
            occurrenceId,
            idempotencyKey,
            fingerprint,
            constraint.Version,
            resultingVersion,
            snapshotJson,
            now,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        commitHook?.AfterCommit(transformed.Value);
        return Result.Success(new AlarmActionResult(transformed.Value, AlarmActionDisposition.Applied));
    }

    private Result<AlarmOccurrenceSnapshot> Acknowledge(
        AuthorizedMutation authorization,
        AlarmOccurrenceSnapshot occurrence,
        AcknowledgeAlarmRequest request)
    {
        if (!request.Constraint.AcknowledgementAllowed)
        {
            return Failure<AlarmOccurrenceSnapshot>(
                "alarm.maintenance_blocked",
                "Maintenance constraint blocks acknowledgement.");
        }

        if (occurrence.Acknowledgement.Version != request.ExpectedVersion)
        {
            return Failure<AlarmOccurrenceSnapshot>("alarm.expected_version", "Acknowledgement version is stale.");
        }

        if (occurrence.Acknowledgement.State == AlarmAcknowledgementState.Acknowledged)
        {
            return Failure<AlarmOccurrenceSnapshot>("alarm.already_acknowledged", "Alarm is already acknowledged.");
        }

        return Result.Success(occurrence with
        {
            Acknowledgement = new AlarmAcknowledgementFacet(
                AlarmAcknowledgementState.Acknowledged,
                authorization.Session.SubjectId.Value,
                clock.GetUtcNow(),
                request.ExpectedVersion.Next()),
        });
    }

    private Result<AlarmOccurrenceSnapshot> Assign(
        AlarmOccurrenceSnapshot occurrence,
        AssignAlarmRequest request)
    {
        var active = RequireActive(occurrence);
        if (active.IsFailure)
        {
            return Result.Failure<AlarmOccurrenceSnapshot>(active.Error!);
        }

        if (!request.Constraint.AssignmentAllowed)
        {
            return Failure<AlarmOccurrenceSnapshot>("alarm.maintenance_blocked", "Maintenance constraint blocks assignment.");
        }

        if (occurrence.Assignment.Version != request.ExpectedVersion)
        {
            return Failure<AlarmOccurrenceSnapshot>("alarm.expected_version", "Assignment version is stale.");
        }

        return Result.Success(occurrence with
        {
            Assignment = new AlarmAssignmentFacet(request.AssignedTo, clock.GetUtcNow(), request.ExpectedVersion.Next()),
        });
    }

    private Result<AlarmOccurrenceSnapshot> Shelve(
        AlarmOccurrenceSnapshot occurrence,
        ShelveAlarmRequest request)
    {
        var active = RequireActive(occurrence);
        if (active.IsFailure)
        {
            return Result.Failure<AlarmOccurrenceSnapshot>(active.Error!);
        }

        if (!request.Constraint.ShelvingAllowed ||
            request.Constraint.WindowEndsAt is not null && request.ShelvedUntil > request.Constraint.WindowEndsAt)
        {
            return Failure<AlarmOccurrenceSnapshot>("alarm.maintenance_blocked", "Maintenance constraint blocks shelving.");
        }

        if (request.ShelvedUntil <= clock.GetUtcNow())
        {
            return Failure<AlarmOccurrenceSnapshot>("alarm.shelving_expired", "Shelving expiry must be in the future.");
        }

        if (occurrence.Shelving.Version != request.ExpectedVersion)
        {
            return Failure<AlarmOccurrenceSnapshot>("alarm.expected_version", "Shelving version is stale.");
        }

        return Result.Success(occurrence with
        {
            Shelving = new AlarmShelvingFacet(
                request.ShelvedUntil,
                request.Reason.Trim(),
                request.ExpectedVersion.Next()),
        });
    }

    private static Result RequireActive(AlarmOccurrenceSnapshot occurrence) =>
        occurrence.ClosedAt is null && occurrence.Condition.State is
            AlarmConditionState.Active or AlarmConditionState.PendingClear
            ? Result.Success()
            : Result.Failure(new OperationError(
                ErrorCode.From("alarm.occurrence_inactive"),
                "Alarm action requires an active occurrence."));

    private static async Task<int> UpdateAcknowledgementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AlarmOccurrenceSnapshot occurrence,
        StateVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {AlarmMigrations.Schema}.occurrence
            SET acknowledgement_state = @state, acknowledged_by = @by,
                acknowledged_at = @at, acknowledgement_version = @next_version
            WHERE scope_id = @scope_id AND occurrence_id = @occurrence_id
              AND acknowledgement_version = @expected_version;
            """,
            connection,
            transaction);
        AddUpdateIdentity(command, occurrence, expectedVersion);
        command.Parameters.AddWithValue("state", (short)occurrence.Acknowledgement.State);
        command.Parameters.AddWithValue("by", occurrence.Acknowledgement.AcknowledgedBy!.Value);
        command.Parameters.AddWithValue("at", occurrence.Acknowledgement.AcknowledgedAt!.Value);
        command.Parameters.AddWithValue("next_version", checked((long)occurrence.Acknowledgement.Version.Value));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> UpdateAssignmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AlarmOccurrenceSnapshot occurrence,
        StateVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {AlarmMigrations.Schema}.occurrence
            SET assigned_to = @assigned_to, assigned_at = @assigned_at,
                assignment_version = @next_version
            WHERE scope_id = @scope_id AND occurrence_id = @occurrence_id
              AND assignment_version = @expected_version;
            """,
            connection,
            transaction);
        AddUpdateIdentity(command, occurrence, expectedVersion);
        command.Parameters.AddWithValue("assigned_to", occurrence.Assignment.AssignedTo!.Value);
        command.Parameters.AddWithValue("assigned_at", occurrence.Assignment.AssignedAt!.Value);
        command.Parameters.AddWithValue("next_version", checked((long)occurrence.Assignment.Version.Value));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> UpdateShelvingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AlarmOccurrenceSnapshot occurrence,
        StateVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {AlarmMigrations.Schema}.occurrence
            SET shelved_until = @shelved_until, shelving_reason = @reason,
                shelving_version = @next_version
            WHERE scope_id = @scope_id AND occurrence_id = @occurrence_id
              AND shelving_version = @expected_version;
            """,
            connection,
            transaction);
        AddUpdateIdentity(command, occurrence, expectedVersion);
        command.Parameters.AddWithValue("shelved_until", occurrence.Shelving.ShelvedUntil!.Value);
        command.Parameters.AddWithValue("reason", occurrence.Shelving.Reason!);
        command.Parameters.AddWithValue("next_version", checked((long)occurrence.Shelving.Version.Value));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddUpdateIdentity(
        NpgsqlCommand command,
        AlarmOccurrenceSnapshot occurrence,
        StateVersion expectedVersion)
    {
        command.Parameters.AddWithValue("scope_id", occurrence.ScopeId.Value);
        command.Parameters.AddWithValue("occurrence_id", occurrence.OccurrenceId.Value);
        command.Parameters.AddWithValue("expected_version", checked((long)expectedVersion.Value));
    }

    private static async Task<AlarmOccurrenceSnapshot?> ReadOccurrenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        AlarmOccurrenceId occurrenceId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT definition_epoch, definition_id, point_id, priority, opened_at, closed_at,
                   condition_state, condition_pending_since, condition_active_since, condition_cleared_at,
                   condition_version, acknowledgement_state, acknowledged_by, acknowledged_at,
                   acknowledgement_version, assigned_to, assigned_at, assignment_version,
                   shelved_until, shelving_reason, shelving_version, is_suppressed,
                   suppression_reason, suppression_version
            FROM {AlarmMigrations.Schema}.occurrence
            WHERE scope_id = @scope_id AND occurrence_id = @occurrence_id FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("occurrence_id", occurrenceId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapOccurrence(reader, scopeId, occurrenceId)
            : null;
    }

    private static AlarmOccurrenceSnapshot MapOccurrence(
        NpgsqlDataReader reader,
        RuntimeScopeId scopeId,
        AlarmOccurrenceId occurrenceId) => new(
        occurrenceId,
        scopeId,
        RevisionNumber.From(checked((ulong)reader.GetInt64(0))),
        AlarmDefinitionId.From(reader.GetGuid(1)),
        PointId.From(reader.GetGuid(2)),
        (AlarmPriority)reader.GetInt16(3),
        reader.GetFieldValue<DateTimeOffset>(4),
        reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        new AlarmConditionFacet(
            (AlarmConditionState)reader.GetInt16(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            StateVersion.From(checked((ulong)reader.GetInt64(10)))),
        new AlarmAcknowledgementFacet(
            (AlarmAcknowledgementState)reader.GetInt16(11),
            reader.IsDBNull(12) ? null : reader.GetGuid(12),
            reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
            StateVersion.From(checked((ulong)reader.GetInt64(14)))),
        new AlarmAssignmentFacet(
            reader.IsDBNull(15) ? null : reader.GetGuid(15),
            reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
            StateVersion.From(checked((ulong)reader.GetInt64(17)))),
        new AlarmShelvingFacet(
            reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            StateVersion.From(checked((ulong)reader.GetInt64(20)))),
        new AlarmSuppressionFacet(
            reader.GetBoolean(21),
            reader.IsDBNull(22) ? null : reader.GetString(22),
            StateVersion.From(checked((ulong)reader.GetInt64(23)))));

    private static async Task<ExistingAction?> ReadActionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SubjectId subjectId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT action_kind, request_fingerprint, result_snapshot::text
            FROM {AlarmMigrations.Schema}.action_request
            WHERE subject_id = @subject_id AND idempotency_key = @idempotency_key;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("subject_id", subjectId.Value);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ExistingAction((AlarmActionKind)reader.GetInt16(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static async Task InsertActionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuthorizedMutation authorization,
        Guid actionId,
        AlarmActionKind action,
        RuntimeScopeId scopeId,
        AlarmOccurrenceId occurrenceId,
        string idempotencyKey,
        string fingerprint,
        StateVersion constraintVersion,
        StateVersion resultingVersion,
        string snapshotJson,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand(
                         $"""
                         INSERT INTO {AlarmMigrations.Schema}.action_request (
                             action_id, scope_id, occurrence_id, session_id, subject_id,
                             idempotency_key, request_fingerprint, action_kind, constraint_version,
                             resulting_facet_version, result_snapshot, accepted_at)
                         VALUES (
                             @action_id, @scope_id, @occurrence_id, @session_id, @subject_id,
                             @idempotency_key, @fingerprint, @action_kind, @constraint_version,
                             @resulting_version, CAST(@snapshot AS jsonb), @changed_at);
                         """,
                         connection,
                         transaction))
        {
            AddActionParameters(
                command,
                authorization,
                actionId,
                action,
                scopeId,
                occurrenceId,
                idempotencyKey,
                fingerprint,
                constraintVersion,
                resultingVersion,
                snapshotJson,
                changedAt);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var audit = new NpgsqlCommand(
            $"""
            INSERT INTO {AlarmMigrations.Schema}.mutation_audit (
                audit_id, action_id, scope_id, occurrence_id, session_id, subject_id,
                permission, action, resulting_facet_version, changed_at)
            VALUES (
                @audit_id, @action_id, @scope_id, @occurrence_id, @session_id, @subject_id,
                @permission, @action, @resulting_version, @changed_at);
            """,
            connection,
            transaction);
        audit.Parameters.AddWithValue("audit_id", Guid.CreateVersion7());
        audit.Parameters.AddWithValue("action_id", actionId);
        audit.Parameters.AddWithValue("scope_id", scopeId.Value);
        audit.Parameters.AddWithValue("occurrence_id", occurrenceId.Value);
        audit.Parameters.AddWithValue("session_id", authorization.Session.Id.Value);
        audit.Parameters.AddWithValue("subject_id", authorization.Session.SubjectId.Value);
        audit.Parameters.AddWithValue("permission", authorization.Permission.Value);
        audit.Parameters.AddWithValue("action", action.ToString());
        audit.Parameters.AddWithValue("resulting_version", checked((long)resultingVersion.Value));
        audit.Parameters.AddWithValue("changed_at", changedAt);
        await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddActionParameters(
        NpgsqlCommand command,
        AuthorizedMutation authorization,
        Guid actionId,
        AlarmActionKind action,
        RuntimeScopeId scopeId,
        AlarmOccurrenceId occurrenceId,
        string idempotencyKey,
        string fingerprint,
        StateVersion constraintVersion,
        StateVersion resultingVersion,
        string snapshotJson,
        DateTimeOffset changedAt)
    {
        command.Parameters.AddWithValue("action_id", actionId);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("occurrence_id", occurrenceId.Value);
        command.Parameters.AddWithValue("session_id", authorization.Session.Id.Value);
        command.Parameters.AddWithValue("subject_id", authorization.Session.SubjectId.Value);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        command.Parameters.AddWithValue("action_kind", (short)action);
        command.Parameters.AddWithValue("constraint_version", checked((long)constraintVersion.Value));
        command.Parameters.AddWithValue("resulting_version", checked((long)resultingVersion.Value));
        command.Parameters.AddWithValue("snapshot", snapshotJson);
        command.Parameters.AddWithValue("changed_at", changedAt);
    }

    private static StateVersion FacetVersion(AlarmActionKind action, AlarmOccurrenceSnapshot occurrence) => action switch
    {
        AlarmActionKind.Acknowledge => occurrence.Acknowledgement.Version,
        AlarmActionKind.Assign => occurrence.Assignment.Version,
        AlarmActionKind.Shelve => occurrence.Shelving.Version,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static PermissionCode Permission(AlarmActionKind action) => action switch
    {
        AlarmActionKind.Acknowledge => AlarmPermissions.Acknowledge,
        AlarmActionKind.Assign => AlarmPermissions.Assign,
        AlarmActionKind.Shelve => AlarmPermissions.Shelve,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static async Task LockScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@scope_id, 0));",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value.ToString("N"));
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

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));

    private enum AlarmActionKind
    {
        Acknowledge = 1,
        Assign = 2,
        Shelve = 3,
    }

    private sealed record ExistingAction(AlarmActionKind Action, string Fingerprint, string SnapshotJson);

    private sealed record OccurrenceDto(
        Guid OccurrenceId,
        Guid ScopeId,
        ulong DefinitionEpoch,
        Guid DefinitionId,
        Guid PointId,
        int Priority,
        DateTimeOffset OpenedAt,
        DateTimeOffset? ClosedAt,
        int ConditionState,
        DateTimeOffset? ConditionPendingSince,
        DateTimeOffset? ConditionActiveSince,
        DateTimeOffset? ConditionClearedAt,
        ulong ConditionVersion,
        int AcknowledgementState,
        Guid? AcknowledgedBy,
        DateTimeOffset? AcknowledgedAt,
        ulong AcknowledgementVersion,
        Guid? AssignedTo,
        DateTimeOffset? AssignedAt,
        ulong AssignmentVersion,
        DateTimeOffset? ShelvedUntil,
        string? ShelvingReason,
        ulong ShelvingVersion,
        bool IsSuppressed,
        string? SuppressionReason,
        ulong SuppressionVersion)
    {
        public static OccurrenceDto From(AlarmOccurrenceSnapshot occurrence) => new(
            occurrence.OccurrenceId.Value,
            occurrence.ScopeId.Value,
            occurrence.DefinitionEpoch.Value,
            occurrence.DefinitionId.Value,
            occurrence.PointId.Value,
            (int)occurrence.Priority,
            occurrence.OpenedAt,
            occurrence.ClosedAt,
            (int)occurrence.Condition.State,
            occurrence.Condition.PendingSince,
            occurrence.Condition.ActiveSince,
            occurrence.Condition.ClearedAt,
            occurrence.Condition.Version.Value,
            (int)occurrence.Acknowledgement.State,
            occurrence.Acknowledgement.AcknowledgedBy,
            occurrence.Acknowledgement.AcknowledgedAt,
            occurrence.Acknowledgement.Version.Value,
            occurrence.Assignment.AssignedTo,
            occurrence.Assignment.AssignedAt,
            occurrence.Assignment.Version.Value,
            occurrence.Shelving.ShelvedUntil,
            occurrence.Shelving.Reason,
            occurrence.Shelving.Version.Value,
            occurrence.Suppression.IsSuppressed,
            occurrence.Suppression.Reason,
            occurrence.Suppression.Version.Value);

        public AlarmOccurrenceSnapshot ToModel() => new(
            AlarmOccurrenceId.From(OccurrenceId),
            RuntimeScopeId.From(ScopeId),
            RevisionNumber.From(DefinitionEpoch),
            AlarmDefinitionId.From(DefinitionId),
            Dispatcher.Semantics.PointId.From(PointId),
            (AlarmPriority)Priority,
            OpenedAt,
            ClosedAt,
            new AlarmConditionFacet(
                (AlarmConditionState)ConditionState,
                ConditionPendingSince,
                ConditionActiveSince,
                ConditionClearedAt,
                StateVersion.From(ConditionVersion)),
            new AlarmAcknowledgementFacet(
                (AlarmAcknowledgementState)AcknowledgementState,
                AcknowledgedBy,
                AcknowledgedAt,
                StateVersion.From(AcknowledgementVersion)),
            new AlarmAssignmentFacet(AssignedTo, AssignedAt, StateVersion.From(AssignmentVersion)),
            new AlarmShelvingFacet(ShelvedUntil, ShelvingReason, StateVersion.From(ShelvingVersion)),
            new AlarmSuppressionFacet(IsSuppressed, SuppressionReason, StateVersion.From(SuppressionVersion)));
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
