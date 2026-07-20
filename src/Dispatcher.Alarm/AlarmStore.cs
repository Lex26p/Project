using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dispatcher.Core;
using Dispatcher.Semantics;
using Npgsql;

namespace Dispatcher.Alarm;

public sealed partial class AlarmStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;

    public AlarmStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock)
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
    }

    public async Task<Result> ActivateDefinitionSetAsync(
        AlarmDefinitionSet definitionSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitionSet);
        var fingerprint = Fingerprint(definitionSet.Definitions);
        var activatedAt = clock.GetUtcNow();
        if (activatedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Alarm definition activation requires a UTC wall clock.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, definitionSet.ScopeId, cancellationToken).ConfigureAwait(false);
        await EnsureScopeAsync(connection, transaction, definitionSet.ScopeId, cancellationToken).ConfigureAwait(false);

        var active = await ReadActiveSetAsync(
            connection,
            transaction,
            definitionSet.ScopeId,
            cancellationToken).ConfigureAwait(false);
        if (active is not null && active.Value.Epoch == definitionSet.Epoch.Value)
        {
            return string.Equals(active.Value.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? Result.Success()
                : Failure("alarm.definition_epoch_conflict", "The active Alarm definition epoch has different content.");
        }

        var expectedEpoch = active is null ? 1UL : checked(active.Value.Epoch + 1);
        if (definitionSet.Epoch.Value != expectedEpoch)
        {
            return Failure("alarm.definition_epoch", "Alarm definition epochs must activate contiguously.");
        }

        await InsertDefinitionSetAsync(
            connection,
            transaction,
            definitionSet,
            fingerprint,
            activatedAt,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<IReadOnlyList<AlarmOccurrenceSnapshot>> ReadOccurrencesAsync(
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var occurrences = await ReadOccurrencesAsync(
            connection,
            transaction,
            scopeId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return occurrences;
    }

    internal async Task<Result<AlarmEvaluationSnapshot>> EvaluateAsync(
        RuntimeScopeId scopeId,
        RevisionNumber definitionEpoch,
        OwnerPosition<SourceLiveness> evaluationPosition,
        IReadOnlyList<CurrentEntry> current,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);

        var scopeState = await ReadScopeStateAsync(
            connection,
            transaction,
            scopeId,
            cancellationToken).ConfigureAwait(false);
        if (scopeState is null || scopeState.Value.ActiveEpoch != definitionEpoch.Value)
        {
            return Failure<AlarmEvaluationSnapshot>(
                "alarm.definition_epoch_inactive",
                "The evaluator definition epoch is not active for this scope.");
        }

        if (evaluationPosition.Value < scopeState.Value.LastEvaluationPosition)
        {
            return Failure<AlarmEvaluationSnapshot>(
                "alarm.evaluation_stale",
                "Alarm evaluation cannot move behind the persisted scope position.");
        }

        if (evaluationPosition.Value > scopeState.Value.LastEvaluationPosition)
        {
            var definitions = await ReadDefinitionsAsync(
                connection,
                transaction,
                scopeId,
                definitionEpoch,
                cancellationToken).ConfigureAwait(false);
            var states = await ReadEvaluatorStatesAsync(
                connection,
                transaction,
                scopeId,
                definitionEpoch,
                cancellationToken).ConfigureAwait(false);
            var currentByPoint = current.ToDictionary(item => item.PointId);
            foreach (var definition in definitions)
            {
                states.TryGetValue(definition.DefinitionId, out var state);
                state ??= EvaluatorState.Normal;
                if (definition.Enabled &&
                    currentByPoint.TryGetValue(definition.PointId, out var entry) &&
                    entry.Quality == DataQuality.Good &&
                    entry.Freshness == Freshness.Fresh)
                {
                    state = await AdvanceAsync(
                        connection,
                        transaction,
                        scopeId,
                        definitionEpoch,
                        definition,
                        state,
                        entry.Value.Value,
                        evaluatedAt,
                        cancellationToken).ConfigureAwait(false);
                }

                await UpsertEvaluatorStateAsync(
                    connection,
                    transaction,
                    scopeId,
                    definitionEpoch,
                    definition.DefinitionId,
                    state,
                    currentByPoint.GetValueOrDefault(definition.PointId)?.Value.Value,
                    currentByPoint.GetValueOrDefault(definition.PointId)?.CurrentPosition.Value ?? 0,
                    evaluatedAt,
                    cancellationToken).ConfigureAwait(false);
            }

            await UpdateEvaluationPositionAsync(
                connection,
                transaction,
                scopeId,
                evaluationPosition,
                cancellationToken).ConfigureAwait(false);
        }

        var occurrences = await ReadOccurrencesAsync(
            connection,
            transaction,
            scopeId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new AlarmEvaluationSnapshot(scopeId, definitionEpoch, evaluationPosition, occurrences));
    }

    private static async Task<EvaluatorState> AdvanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        RevisionNumber epoch,
        AlarmDefinition definition,
        EvaluatorState state,
        long value,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken)
    {
        var shouldRaise = definition.Direction == AlarmThresholdDirection.High
            ? value >= definition.Threshold
            : value <= definition.Threshold;
        var shouldClear = definition.Direction == AlarmThresholdDirection.High
            ? (decimal)value <= (decimal)definition.Threshold - definition.Hysteresis
            : (decimal)value >= (decimal)definition.Threshold + definition.Hysteresis;

        switch (state.State)
        {
            case AlarmConditionState.Normal when shouldRaise && definition.RaiseDelay == TimeSpan.Zero:
                return await OpenOccurrenceAsync(
                    connection,
                    transaction,
                    scopeId,
                    epoch,
                    definition,
                    evaluatedAt,
                    cancellationToken).ConfigureAwait(false);
            case AlarmConditionState.Normal when shouldRaise:
                return new EvaluatorState(AlarmConditionState.PendingRaise, evaluatedAt, null);
            case AlarmConditionState.PendingRaise when !shouldRaise:
                return EvaluatorState.Normal;
            case AlarmConditionState.PendingRaise when
                evaluatedAt - state.PendingSince!.Value >= definition.RaiseDelay:
                return await OpenOccurrenceAsync(
                    connection,
                    transaction,
                    scopeId,
                    epoch,
                    definition,
                    evaluatedAt,
                    cancellationToken).ConfigureAwait(false);
            case AlarmConditionState.Active when shouldClear && definition.ClearDelay == TimeSpan.Zero:
                await CloseOccurrenceAsync(
                    connection,
                    transaction,
                    state.OccurrenceId!.Value,
                    evaluatedAt,
                    cancellationToken).ConfigureAwait(false);
                return EvaluatorState.Normal;
            case AlarmConditionState.Active when shouldClear:
                await UpdateOccurrenceConditionAsync(
                    connection,
                    transaction,
                    state.OccurrenceId!.Value,
                    AlarmConditionState.PendingClear,
                    evaluatedAt,
                    cancellationToken).ConfigureAwait(false);
                return new EvaluatorState(AlarmConditionState.PendingClear, evaluatedAt, state.OccurrenceId);
            case AlarmConditionState.PendingClear when !shouldClear:
                await UpdateOccurrenceConditionAsync(
                    connection,
                    transaction,
                    state.OccurrenceId!.Value,
                    AlarmConditionState.Active,
                    null,
                    cancellationToken).ConfigureAwait(false);
                return new EvaluatorState(AlarmConditionState.Active, null, state.OccurrenceId);
            case AlarmConditionState.PendingClear when
                evaluatedAt - state.PendingSince!.Value >= definition.ClearDelay:
                await CloseOccurrenceAsync(
                    connection,
                    transaction,
                    state.OccurrenceId!.Value,
                    evaluatedAt,
                    cancellationToken).ConfigureAwait(false);
                return EvaluatorState.Normal;
            default:
                return state;
        }
    }

    private static async Task<EvaluatorState> OpenOccurrenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        RevisionNumber epoch,
        AlarmDefinition definition,
        DateTimeOffset openedAt,
        CancellationToken cancellationToken)
    {
        var occurrenceId = AlarmOccurrenceId.New();
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {AlarmMigrations.Schema}.occurrence (
                occurrence_id, scope_id, definition_epoch, definition_id, point_id, priority,
                opened_at, condition_state, condition_active_since, condition_version,
                acknowledgement_state, acknowledgement_version, assignment_version,
                shelving_version, is_suppressed, suppression_version)
            VALUES (
                @occurrence_id, @scope_id, @definition_epoch, @definition_id, @point_id, @priority,
                @opened_at, @condition_state, @opened_at, 1, 1, 1, 1, 1, false, 1);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("occurrence_id", occurrenceId.Value);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("definition_epoch", checked((long)epoch.Value));
        command.Parameters.AddWithValue("definition_id", definition.DefinitionId.Value);
        command.Parameters.AddWithValue("point_id", definition.PointId.Value);
        command.Parameters.AddWithValue("priority", (short)definition.Priority);
        command.Parameters.AddWithValue("opened_at", openedAt);
        command.Parameters.AddWithValue("condition_state", (short)AlarmConditionState.Active);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new EvaluatorState(AlarmConditionState.Active, null, occurrenceId);
    }

    private static async Task UpdateOccurrenceConditionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AlarmOccurrenceId occurrenceId,
        AlarmConditionState state,
        DateTimeOffset? pendingSince,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {AlarmMigrations.Schema}.occurrence
            SET condition_state = @condition_state,
                condition_pending_since = @pending_since,
                condition_version = condition_version + 1
            WHERE occurrence_id = @occurrence_id AND closed_at IS NULL;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("condition_state", (short)state);
        command.Parameters.AddWithValue("pending_since", (object?)pendingSince ?? DBNull.Value);
        command.Parameters.AddWithValue("occurrence_id", occurrenceId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The active Alarm occurrence is missing.");
        }
    }

    private static async Task CloseOccurrenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AlarmOccurrenceId occurrenceId,
        DateTimeOffset clearedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {AlarmMigrations.Schema}.occurrence
            SET closed_at = @cleared_at,
                condition_state = @condition_state,
                condition_pending_since = NULL,
                condition_cleared_at = @cleared_at,
                condition_version = condition_version + 1
            WHERE occurrence_id = @occurrence_id AND closed_at IS NULL;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("cleared_at", clearedAt);
        command.Parameters.AddWithValue("condition_state", (short)AlarmConditionState.Normal);
        command.Parameters.AddWithValue("occurrence_id", occurrenceId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The active Alarm occurrence is missing.");
        }
    }

    private static async Task UpsertEvaluatorStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        RevisionNumber epoch,
        AlarmDefinitionId definitionId,
        EvaluatorState state,
        long? value,
        ulong currentPosition,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {AlarmMigrations.Schema}.evaluator_state (
                scope_id, definition_epoch, definition_id, condition_state, pending_since,
                current_occurrence_id, last_value, last_current_position, updated_at)
            VALUES (
                @scope_id, @definition_epoch, @definition_id, @condition_state, @pending_since,
                @current_occurrence_id, @last_value, @last_current_position, @updated_at)
            ON CONFLICT (scope_id, definition_epoch, definition_id) DO UPDATE
            SET condition_state = EXCLUDED.condition_state,
                pending_since = EXCLUDED.pending_since,
                current_occurrence_id = EXCLUDED.current_occurrence_id,
                last_value = EXCLUDED.last_value,
                last_current_position = EXCLUDED.last_current_position,
                updated_at = EXCLUDED.updated_at;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("definition_epoch", checked((long)epoch.Value));
        command.Parameters.AddWithValue("definition_id", definitionId.Value);
        command.Parameters.AddWithValue("condition_state", (short)state.State);
        command.Parameters.AddWithValue("pending_since", (object?)state.PendingSince ?? DBNull.Value);
        command.Parameters.AddWithValue("current_occurrence_id", (object?)state.OccurrenceId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("last_value", (object?)value ?? DBNull.Value);
        command.Parameters.AddWithValue("last_current_position", checked((long)currentPosition));
        command.Parameters.AddWithValue("updated_at", updatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<AlarmDefinition>> ReadDefinitionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        RevisionNumber epoch,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT definition_id, point_id, name, direction, threshold, hysteresis,
                   raise_delay_ticks, clear_delay_ticks, enabled, priority
            FROM {AlarmMigrations.Schema}.definition
            WHERE scope_id = @scope_id AND epoch = @epoch
            ORDER BY definition_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("epoch", checked((long)epoch.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var definitions = new List<AlarmDefinition>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            definitions.Add(new AlarmDefinition(
                AlarmDefinitionId.From(reader.GetGuid(0)),
                PointId.From(reader.GetGuid(1)),
                reader.GetString(2),
                (AlarmThresholdDirection)reader.GetInt16(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                TimeSpan.FromTicks(reader.GetInt64(6)),
                TimeSpan.FromTicks(reader.GetInt64(7)),
                reader.GetBoolean(8),
                (AlarmPriority)reader.GetInt16(9)));
        }

        return definitions;
    }

    private static async Task<Dictionary<AlarmDefinitionId, EvaluatorState>> ReadEvaluatorStatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        RevisionNumber epoch,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT definition_id, condition_state, pending_since, current_occurrence_id
            FROM {AlarmMigrations.Schema}.evaluator_state
            WHERE scope_id = @scope_id AND definition_epoch = @epoch;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("epoch", checked((long)epoch.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var states = new Dictionary<AlarmDefinitionId, EvaluatorState>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            states.Add(
                AlarmDefinitionId.From(reader.GetGuid(0)),
                new EvaluatorState(
                    (AlarmConditionState)reader.GetInt16(1),
                    reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                    reader.IsDBNull(3) ? null : AlarmOccurrenceId.From(reader.GetGuid(3))));
        }

        return states;
    }

    private static async Task<IReadOnlyList<AlarmOccurrenceSnapshot>> ReadOccurrencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT occurrence_id, definition_epoch, definition_id, point_id, priority, opened_at, closed_at,
                   condition_state, condition_pending_since, condition_active_since, condition_cleared_at,
                   condition_version, acknowledgement_state, acknowledged_by, acknowledged_at,
                   acknowledgement_version, assigned_to, assigned_at, assignment_version,
                   shelved_until, shelving_reason, shelving_version, is_suppressed,
                   suppression_reason, suppression_version
            FROM {AlarmMigrations.Schema}.occurrence
            WHERE scope_id = @scope_id
            ORDER BY opened_at, occurrence_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var occurrences = new List<AlarmOccurrenceSnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            occurrences.Add(new AlarmOccurrenceSnapshot(
                AlarmOccurrenceId.From(reader.GetGuid(0)),
                scopeId,
                RevisionNumber.From(checked((ulong)reader.GetInt64(1))),
                AlarmDefinitionId.From(reader.GetGuid(2)),
                PointId.From(reader.GetGuid(3)),
                (AlarmPriority)reader.GetInt16(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                new AlarmConditionFacet(
                    (AlarmConditionState)reader.GetInt16(7),
                    reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                    reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                    reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                    StateVersion.From(checked((ulong)reader.GetInt64(11)))),
                new AlarmAcknowledgementFacet(
                    (AlarmAcknowledgementState)reader.GetInt16(12),
                    reader.IsDBNull(13) ? null : reader.GetGuid(13),
                    reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
                    StateVersion.From(checked((ulong)reader.GetInt64(15)))),
                new AlarmAssignmentFacet(
                    reader.IsDBNull(16) ? null : reader.GetGuid(16),
                    reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
                    StateVersion.From(checked((ulong)reader.GetInt64(18)))),
                new AlarmShelvingFacet(
                    reader.IsDBNull(19) ? null : reader.GetFieldValue<DateTimeOffset>(19),
                    reader.IsDBNull(20) ? null : reader.GetString(20),
                    StateVersion.From(checked((ulong)reader.GetInt64(21)))),
                new AlarmSuppressionFacet(
                    reader.GetBoolean(22),
                    reader.IsDBNull(23) ? null : reader.GetString(23),
                    StateVersion.From(checked((ulong)reader.GetInt64(24))))));
        }

        return occurrences;
    }

    private static async Task InsertDefinitionSetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AlarmDefinitionSet definitionSet,
        string fingerprint,
        DateTimeOffset activatedAt,
        CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand(
                         $"""
                         INSERT INTO {AlarmMigrations.Schema}.definition_set
                             (scope_id, epoch, fingerprint, activated_at)
                         VALUES (@scope_id, @epoch, @fingerprint, @activated_at);
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("scope_id", definitionSet.ScopeId.Value);
            command.Parameters.AddWithValue("epoch", checked((long)definitionSet.Epoch.Value));
            command.Parameters.AddWithValue("fingerprint", fingerprint);
            command.Parameters.AddWithValue("activated_at", activatedAt);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var definition in definitionSet.Definitions)
        {
            await using var command = new NpgsqlCommand(
                $"""
                INSERT INTO {AlarmMigrations.Schema}.definition (
                    scope_id, epoch, definition_id, point_id, name, direction, threshold,
                    hysteresis, raise_delay_ticks, clear_delay_ticks, enabled, priority)
                VALUES (
                    @scope_id, @epoch, @definition_id, @point_id, @name, @direction, @threshold,
                    @hysteresis, @raise_delay_ticks, @clear_delay_ticks, @enabled, @priority);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("scope_id", definitionSet.ScopeId.Value);
            command.Parameters.AddWithValue("epoch", checked((long)definitionSet.Epoch.Value));
            command.Parameters.AddWithValue("definition_id", definition.DefinitionId.Value);
            command.Parameters.AddWithValue("point_id", definition.PointId.Value);
            command.Parameters.AddWithValue("name", definition.Name);
            command.Parameters.AddWithValue("direction", (short)definition.Direction);
            command.Parameters.AddWithValue("threshold", definition.Threshold);
            command.Parameters.AddWithValue("hysteresis", definition.Hysteresis);
            command.Parameters.AddWithValue("raise_delay_ticks", definition.RaiseDelay.Ticks);
            command.Parameters.AddWithValue("clear_delay_ticks", definition.ClearDelay.Ticks);
            command.Parameters.AddWithValue("enabled", definition.Enabled);
            command.Parameters.AddWithValue("priority", (short)definition.Priority);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var update = new NpgsqlCommand(
            $"UPDATE {AlarmMigrations.Schema}.scope_state SET active_epoch = @epoch WHERE scope_id = @scope_id;",
            connection,
            transaction);
        update.Parameters.AddWithValue("epoch", checked((long)definitionSet.Epoch.Value));
        update.Parameters.AddWithValue("scope_id", definitionSet.ScopeId.Value);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(ulong Epoch, string Fingerprint)?> ReadActiveSetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT state.active_epoch, definitions.fingerprint
            FROM {AlarmMigrations.Schema}.scope_state state
            LEFT JOIN {AlarmMigrations.Schema}.definition_set definitions
              ON definitions.scope_id = state.scope_id AND definitions.epoch = state.active_epoch
            WHERE state.scope_id = @scope_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return null;
        }

        return (checked((ulong)reader.GetInt64(0)), reader.GetString(1));
    }

    private static async Task<(ulong ActiveEpoch, ulong LastEvaluationPosition)?> ReadScopeStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT active_epoch, last_evaluation_position
            FROM {AlarmMigrations.Schema}.scope_state
            WHERE scope_id = @scope_id FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return null;
        }

        return (checked((ulong)reader.GetInt64(0)), checked((ulong)reader.GetInt64(1)));
    }

    private static async Task UpdateEvaluationPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        OwnerPosition<SourceLiveness> position,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {AlarmMigrations.Schema}.scope_state
            SET last_evaluation_position = @position
            WHERE scope_id = @scope_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("position", checked((long)position.Value));
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {AlarmMigrations.Schema}.scope_state (scope_id) VALUES (@scope_id) ON CONFLICT DO NOTHING;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

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

    private static string Fingerprint(IReadOnlyList<AlarmDefinition> definitions)
    {
        var canonical = definitions.Select(item => new
        {
            DefinitionId = item.DefinitionId.Value,
            PointId = item.PointId.Value,
            item.Name,
            Direction = (int)item.Direction,
            item.Threshold,
            item.Hysteresis,
            RaiseDelayTicks = item.RaiseDelay.Ticks,
            ClearDelayTicks = item.ClearDelay.Ticks,
            item.Enabled,
            Priority = (int)item.Priority,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical))));
    }

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));

    private sealed record EvaluatorState(
        AlarmConditionState State,
        DateTimeOffset? PendingSince,
        AlarmOccurrenceId? OccurrenceId)
    {
        public static EvaluatorState Normal { get; } = new(AlarmConditionState.Normal, null, null);
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
