using System.Text.Json;
using Dispatcher.Semantics;
using Npgsql;
using NpgsqlTypes;

namespace Dispatcher.Core;

public sealed partial class CoreRuntimeStore
{
    public async Task<Result<RuntimeProcessingDelivery>> SaveCheckpointWithPendingDeliveryAsync(
        CoreRuntimeCheckpoint checkpoint,
        RuntimeSourceObligation obligation,
        RuntimeCutAcceptance? postCutAcceptance,
        bool protectedContinuity,
        Guid? definitionEpoch = null,
        RevisionNumber? alarmDefinitionEpoch = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(obligation);
        if (alarmDefinitionEpoch is { } epoch && !epoch.IsDefined)
        {
            return DeliveryFailure(
                "runtime.delivery_alarm_epoch",
                "Alarm definition epoch must be defined when supplied.");
        }

        var validation = ValidatePendingDelivery(checkpoint, obligation, postCutAcceptance);
        if (validation.IsFailure)
        {
            return Result.Failure<RuntimeProcessingDelivery>(validation.Error!);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, checkpoint.ScopeId, cancellationToken).ConfigureAwait(false);
        await EnsureScopeAsync(connection, transaction, checkpoint.ScopeId, cancellationToken).ConfigureAwait(false);

        var scopePosition = await ReadScopeCheckpointPositionAsync(
            connection,
            transaction,
            checkpoint.ScopeId,
            cancellationToken).ConfigureAwait(false);
        var requestedPosition = checked((long)obligation.Position.Value);
        if (requestedPosition != checked(scopePosition.CheckpointPosition + 1) ||
            requestedPosition >= scopePosition.NextObligationPosition)
        {
            return DeliveryFailure(
                "runtime.delivery_position",
                "Processing delivery must be created for the next protected obligation.");
        }

        var storedFact = await ReadStoredObligationStateAsync(
            connection,
            transaction,
            obligation,
            cancellationToken).ConfigureAwait(false);
        if (storedFact is null)
        {
            return DeliveryFailure(
                "runtime.delivery_obligation_missing",
                "The protected obligation for processing delivery does not exist.");
        }

        if (storedFact.FactClass != obligation.FactClass)
        {
            return DeliveryFailure(
                "runtime.delivery_obligation_conflict",
                "The stored protected obligation has another fact class.");
        }

        if (storedFact.Checkpointed)
        {
            return DeliveryFailure(
                "runtime.delivery_obligation_checkpointed",
                "The protected obligation was already checkpointed.");
        }

        var unfinishedPosition = await ReadUnfinishedDeliveryPositionAsync(
            connection,
            transaction,
            checkpoint.ScopeId,
            cancellationToken).ConfigureAwait(false);
        if (unfinishedPosition is not null)
        {
            return DeliveryFailure(
                "runtime.delivery_pending",
                "A previous processing delivery must complete before the next obligation.");
        }

        var now = clock.GetUtcNow();
        var acceptancePayload = postCutAcceptance is null
            ? null
            : JsonSerializer.Serialize(ProcessingAcceptanceDto.From(postCutAcceptance));
        await using (var command = new NpgsqlCommand(
                         $"""
                         UPDATE {CoreRuntimeMigrations.Schema}.scope_state
                         SET checkpoint_obligation_position = @position,
                             protected_continuity = @protected_continuity,
                             checkpoint = @checkpoint,
                             checkpointed_at = @checkpointed_at
                         WHERE scope_id = @scope_id;

                         UPDATE {CoreRuntimeMigrations.Schema}.source_obligation
                         SET checkpointed_at = @checkpointed_at
                         WHERE scope_id = @scope_id AND obligation_position = @position;

                         INSERT INTO {CoreRuntimeMigrations.Schema}.processing_delivery
                             (scope_id, obligation_position, fact_class, stage,
                              post_cut_acceptance, gap_reason, definition_epoch,
                              alarm_definition_epoch, created_at, updated_at)
                         VALUES
                             (@scope_id, @position, @fact_class, @stage,
                              @post_cut_acceptance, @gap_reason, @definition_epoch,
                              @alarm_definition_epoch, @created_at, @updated_at);
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("position", requestedPosition);
            command.Parameters.AddWithValue("protected_continuity", protectedContinuity);
            command.Parameters.AddWithValue(
                "checkpoint",
                NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(CheckpointDto.From(checkpoint)));
            command.Parameters.AddWithValue("checkpointed_at", now);
            command.Parameters.AddWithValue("scope_id", checkpoint.ScopeId.Value);
            command.Parameters.AddWithValue("fact_class", (short)obligation.FactClass);
            command.Parameters.AddWithValue(
                "stage",
                (short)RuntimeProcessingDeliveryStage.PendingDownstream);
            command.Parameters.AddWithValue(
                "post_cut_acceptance",
                NpgsqlDbType.Jsonb,
                (object?)acceptancePayload ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "gap_reason",
                (object?)obligation.Gap?.Reason ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "definition_epoch",
                NpgsqlDbType.Uuid,
                (object?)definitionEpoch ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "alarm_definition_epoch",
                NpgsqlDbType.Bigint,
                alarmDefinitionEpoch is null
                    ? DBNull.Value
                    : checked((long)alarmDefinitionEpoch.Value.Value));
            command.Parameters.AddWithValue("created_at", now);
            command.Parameters.AddWithValue("updated_at", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new RuntimeProcessingDelivery(
            obligation with { CheckpointedAt = now },
            RuntimeProcessingDeliveryStage.PendingDownstream,
            postCutAcceptance,
            obligation.Gap?.Reason,
            definitionEpoch,
            RuntimeDownstreamDeliveryState.Pending,
            RuntimeDownstreamDeliveryState.Pending,
            RuntimeDownstreamDeliveryState.Pending,
            null,
            null,
            now,
            now,
            null,
            null,
            alarmDefinitionEpoch));
    }

    public async Task<RuntimeProcessingDelivery?> LoadPendingDeliveryAsync(
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        DeliveryRow? row = null;
        await using (var command = new NpgsqlCommand(
                         $"""
                         SELECT obligation_position, stage, post_cut_acceptance::text,
                                gap_reason, definition_epoch, history_state, alarm_state,
                                event_state, last_error_code, last_error_at, created_at,
                                updated_at, downstream_completed_at, published_at,
                                alarm_definition_epoch
                         FROM {CoreRuntimeMigrations.Schema}.processing_delivery
                         WHERE scope_id = @scope_id AND stage < 3
                         ORDER BY obligation_position
                         LIMIT 1;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                row = new DeliveryRow(
                    reader.GetInt64(0),
                    (RuntimeProcessingDeliveryStage)reader.GetInt16(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    (RuntimeDownstreamDeliveryState)reader.GetInt16(5),
                    (RuntimeDownstreamDeliveryState)reader.GetInt16(6),
                    (RuntimeDownstreamDeliveryState)reader.GetInt16(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                    reader.GetFieldValue<DateTimeOffset>(10),
                    reader.GetFieldValue<DateTimeOffset>(11),
                    reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                    reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
                    reader.IsDBNull(14)
                        ? null
                        : RevisionNumber.From(
                            checked((ulong)reader.GetInt64(14))));
            }
        }

        if (row is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        RuntimeSourceObligation obligation;
        await using (var command = CreateObligationQuery(
                         connection,
                         transaction,
                         "scope_id = @scope_id AND obligation_position = @position"))
        {
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("position", row.ObligationPosition);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "A processing delivery references a missing protected obligation.");
            }

            obligation = MapObligation(reader);
        }

        var acceptance = row.PostCutAcceptance is null
            ? null
            : JsonSerializer.Deserialize<ProcessingAcceptanceDto>(row.PostCutAcceptance)!
                .ToModel(obligation);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RuntimeProcessingDelivery(
            obligation,
            row.Stage,
            acceptance,
            row.GapReason,
            row.DefinitionEpoch,
            row.HistoryState,
            row.AlarmState,
            row.EventState,
            row.LastErrorCode,
            row.LastErrorAt,
            row.CreatedAt,
            row.UpdatedAt,
            row.DownstreamCompletedAt,
            row.PublishedAt,
            row.AlarmDefinitionEpoch);
    }

    private static Result ValidatePendingDelivery(
        CoreRuntimeCheckpoint checkpoint,
        RuntimeSourceObligation obligation,
        RuntimeCutAcceptance? postCutAcceptance)
    {
        if (checkpoint.ScopeId != obligation.ScopeId)
        {
            return DeliveryFailureResult(
                "runtime.delivery_scope_mismatch",
                "Checkpoint and protected obligation belong to different scopes.");
        }

        if (obligation.FactClass == RuntimeFactClass.SourceCut)
        {
            if (obligation.Cut is null ||
                postCutAcceptance is null ||
                postCutAcceptance.Cut.Binding != obligation.Binding ||
                postCutAcceptance.Cut.ScheduleSequence != obligation.ScheduleSequence)
            {
                return DeliveryFailureResult(
                    "runtime.delivery_acceptance",
                    "A cut delivery requires its exact post-cut acceptance.");
            }

            return Result.Success();
        }

        if (obligation.FactClass == RuntimeFactClass.SourceGap &&
            obligation.Gap is not null &&
            postCutAcceptance is null)
        {
            return Result.Success();
        }

        return DeliveryFailureResult(
            "runtime.delivery_acceptance",
            "A gap delivery cannot contain post-cut acceptance.");
    }

    private static async Task<ScopePosition> ReadScopeCheckpointPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT next_obligation_position, checkpoint_obligation_position
            FROM {CoreRuntimeMigrations.Schema}.scope_state
            WHERE scope_id = @scope_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new ScopePosition(reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<StoredObligationState?> ReadStoredObligationStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeSourceObligation obligation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT fact_class, checkpointed_at IS NOT NULL
            FROM {CoreRuntimeMigrations.Schema}.source_obligation
            WHERE scope_id = @scope_id AND obligation_position = @position
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", obligation.ScopeId.Value);
        command.Parameters.AddWithValue(
            "position",
            checked((long)obligation.Position.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredObligationState((RuntimeFactClass)reader.GetInt16(0), reader.GetBoolean(1))
            : null;
    }

    private static async Task<long?> ReadUnfinishedDeliveryPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT obligation_position
            FROM {CoreRuntimeMigrations.Schema}.processing_delivery
            WHERE scope_id = @scope_id AND stage < 3
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : (long)value;
    }

    private static Result<RuntimeProcessingDelivery> DeliveryFailure(string code, string message) =>
        Result.Failure<RuntimeProcessingDelivery>(
            new OperationError(ErrorCode.From(code), message));

    private static Result DeliveryFailureResult(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private sealed record ScopePosition(long NextObligationPosition, long CheckpointPosition);

    private sealed record StoredObligationState(RuntimeFactClass FactClass, bool Checkpointed);

    private sealed record DeliveryRow(
        long ObligationPosition,
        RuntimeProcessingDeliveryStage Stage,
        string? PostCutAcceptance,
        string? GapReason,
        Guid? DefinitionEpoch,
        RuntimeDownstreamDeliveryState HistoryState,
        RuntimeDownstreamDeliveryState AlarmState,
        RuntimeDownstreamDeliveryState EventState,
        string? LastErrorCode,
        DateTimeOffset? LastErrorAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? DownstreamCompletedAt,
        DateTimeOffset? PublishedAt,
        RevisionNumber? AlarmDefinitionEpoch);

    private sealed record ProcessingAcceptanceDto(
        CurrentDto[] CurrentTransitions,
        LivenessDto Liveness)
    {
        public static ProcessingAcceptanceDto From(RuntimeCutAcceptance acceptance) => new(
            acceptance.CurrentTransitions.Select(CurrentDto.From).ToArray(),
            LivenessDto.From(acceptance.Liveness));

        public RuntimeCutAcceptance ToModel(RuntimeSourceObligation obligation)
        {
            if (obligation.Cut is null)
            {
                throw new InvalidOperationException(
                    "A post-cut acceptance cannot be restored for a gap obligation.");
            }

            return new RuntimeCutAcceptance(
                obligation.Cut,
                CurrentTransitions.Select(item => item.ToModel(obligation.ScopeId)).ToArray(),
                Liveness.ToModel(obligation.ScopeId));
        }
    }
}
