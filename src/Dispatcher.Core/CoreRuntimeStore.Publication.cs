using System.Text.Json;
using Dispatcher.Semantics;
using Npgsql;
using NpgsqlTypes;

namespace Dispatcher.Core;

public sealed partial class CoreRuntimeStore
{
    public async Task<Result> CompleteDownstreamAsync(
        RuntimeScopeId scopeId,
        OwnerPosition<RuntimeSourceObligation> obligationPosition,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(obligationPosition.Value);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);

        var stage = await ReadDeliveryStageAsync(
            connection,
            transaction,
            scopeId,
            obligationPosition,
            cancellationToken).ConfigureAwait(false);
        if (stage is null)
        {
            return DeliveryFailureResult(
                "runtime.delivery_missing",
                "The processing delivery does not exist.");
        }

        if ((int)stage.Value >= (int)RuntimeProcessingDeliveryStage.DownstreamCompleted)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }

        var now = clock.GetUtcNow();
        await using (var command = new NpgsqlCommand(
                         $"""
                         UPDATE {CoreRuntimeMigrations.Schema}.processing_delivery
                         SET stage = @stage,
                             history_state = @completed,
                             alarm_state = @completed,
                             event_state = @completed,
                             downstream_completed_at = @completed_at,
                             updated_at = @updated_at,
                             last_error_code = NULL,
                             last_error_at = NULL
                         WHERE scope_id = @scope_id
                           AND obligation_position = @position
                           AND stage = @pending;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue(
                "stage",
                (short)RuntimeProcessingDeliveryStage.DownstreamCompleted);
            command.Parameters.AddWithValue(
                "completed",
                (short)RuntimeDownstreamDeliveryState.Completed);
            command.Parameters.AddWithValue("completed_at", now);
            command.Parameters.AddWithValue("updated_at", now);
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue(
                "position",
                checked((long)obligationPosition.Value));
            command.Parameters.AddWithValue(
                "pending",
                (short)RuntimeProcessingDeliveryStage.PendingDownstream);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return DeliveryFailureResult(
                    "runtime.delivery_state",
                    "The processing delivery changed while downstream completion was committed.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<RuntimePublicationCommit>> PublishCompletedDeliveryAsync(
        RuntimeScopeId scopeId,
        OwnerPosition<RuntimeSourceObligation> obligationPosition,
        int retainedDeltaCapacity,
        bool ready,
        string? degradationReasonCode = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(obligationPosition.Value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedDeltaCapacity);
        var reasonValidation = ValidateDegradationReason(ready, degradationReasonCode);
        if (reasonValidation.IsFailure)
        {
            return Result.Failure<RuntimePublicationCommit>(reasonValidation.Error!);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        await EnsureScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);

        var deliveryState = await ReadDeliveryPublicationStateAsync(
            connection,
            transaction,
            scopeId,
            obligationPosition,
            cancellationToken).ConfigureAwait(false);
        if (deliveryState is null)
        {
            return PublicationFailure(
                "runtime.delivery_missing",
                "The processing delivery does not exist.");
        }

        var publishedState = await ReadPublishedScopeStateAsync(
            connection,
            transaction,
            scopeId,
            cancellationToken).ConfigureAwait(false);
        if (deliveryState.Stage == RuntimeProcessingDeliveryStage.Published)
        {
            var retainedCount = await CountRetainedDeltaAsync(
                connection,
                transaction,
                scopeId,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(new RuntimePublicationCommit(
                RuntimePublicationCommitStatus.AlreadyPublished,
                scopeId,
                obligationPosition,
                new ConsumerCursor<CurrentEntry>(
                    checked((ulong)(publishedState?.CurrentPosition ?? 0))),
                0,
                retainedCount));
        }

        if (deliveryState.Stage != RuntimeProcessingDeliveryStage.DownstreamCompleted)
        {
            return PublicationFailure(
                "runtime.delivery_downstream_pending",
                "The processing delivery cannot publish before downstream completion.");
        }

        var completedPosition = publishedState?.CompletedObligationPosition ?? 0;
        var requestedPosition = checked((long)obligationPosition.Value);
        if (requestedPosition != checked(completedPosition + 1))
        {
            return PublicationFailure(
                "runtime.delivery_publish_order",
                "Processing delivery publication must follow obligation order.");
        }

        RuntimeSourceObligation obligation;
        await using (var command = CreateObligationQuery(
                         connection,
                         transaction,
                         "scope_id = @scope_id AND obligation_position = @position"))
        {
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("position", requestedPosition);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return PublicationFailure(
                    "runtime.delivery_obligation_missing",
                    "The processing delivery references a missing protected obligation.");
            }

            obligation = MapObligation(reader);
        }

        var transitions = Array.Empty<CurrentEntry>();
        if (obligation.FactClass == RuntimeFactClass.SourceCut)
        {
            if (deliveryState.PostCutAcceptance is null)
            {
                return PublicationFailure(
                    "runtime.delivery_acceptance",
                    "A cut processing delivery is missing post-cut acceptance.");
            }

            transitions = JsonSerializer
                .Deserialize<ProcessingAcceptanceDto>(deliveryState.PostCutAcceptance)!
                .ToModel(obligation)
                .CurrentTransitions
                .OrderBy(item => item.CurrentPosition.Value)
                .ToArray();
        }

        var currentPosition = checked((ulong)(publishedState?.CurrentPosition ?? 0));
        var transitionValidation = ValidatePublicationTransitions(scopeId, currentPosition, transitions);
        if (transitionValidation.IsFailure)
        {
            return Result.Failure<RuntimePublicationCommit>(transitionValidation.Error!);
        }

        var finalCurrentPosition = transitions.Length == 0
            ? currentPosition
            : transitions[^1].CurrentPosition.Value;
        var scopeContinuity = await ReadProtectedContinuityAsync(
            connection,
            transaction,
            scopeId,
            cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        await UpsertPublishedScopeAsync(
            connection,
            transaction,
            scopeId,
            requestedPosition,
            finalCurrentPosition,
            scopeContinuity,
            ready,
            degradationReasonCode,
            now,
            cancellationToken).ConfigureAwait(false);

        foreach (var transition in transitions)
        {
            await UpsertPublishedTransitionAsync(
                connection,
                transaction,
                transition,
                cancellationToken).ConfigureAwait(false);
        }

        currentPosition = finalCurrentPosition;
        await PrunePublishedDeltaAsync(
            connection,
            transaction,
            scopeId,
            retainedDeltaCapacity,
            cancellationToken).ConfigureAwait(false);
        var retainedDeltaCount = await CountRetainedDeltaAsync(
            connection,
            transaction,
            scopeId,
            cancellationToken).ConfigureAwait(false);
        var earliestDeltaPosition = await ReadEarliestDeltaPositionAsync(
            connection,
            transaction,
            scopeId,
            currentPosition,
            cancellationToken).ConfigureAwait(false);

        await using (var command = new NpgsqlCommand(
                         $"""
                         UPDATE {CoreRuntimeMigrations.Schema}.published_scope
                         SET earliest_delta_position = @earliest_delta_position
                         WHERE scope_id = @scope_id;

                         UPDATE {CoreRuntimeMigrations.Schema}.processing_delivery
                         SET stage = @published_stage,
                             published_at = @published_at,
                             updated_at = @updated_at
                         WHERE scope_id = @scope_id
                           AND obligation_position = @position
                           AND stage = @completed_stage;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue(
                "earliest_delta_position",
                checked((long)earliestDeltaPosition));
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue(
                "published_stage",
                (short)RuntimeProcessingDeliveryStage.Published);
            command.Parameters.AddWithValue("published_at", now);
            command.Parameters.AddWithValue("updated_at", now);
            command.Parameters.AddWithValue("position", requestedPosition);
            command.Parameters.AddWithValue(
                "completed_stage",
                (short)RuntimeProcessingDeliveryStage.DownstreamCompleted);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 2)
            {
                return PublicationFailure(
                    "runtime.delivery_state",
                    "The processing delivery changed while publication was committed.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new RuntimePublicationCommit(
            RuntimePublicationCommitStatus.Published,
            scopeId,
            obligationPosition,
            new ConsumerCursor<CurrentEntry>(currentPosition),
            transitions.Length,
            retainedDeltaCount));
    }

    private static Result ValidateDegradationReason(
        bool ready,
        string? degradationReasonCode)
    {
        if (ready && degradationReasonCode is not null)
        {
            return DeliveryFailureResult(
                "runtime.publication_readiness",
                "A ready published scope cannot contain a degradation reason.");
        }

        if (degradationReasonCode is null)
        {
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(degradationReasonCode) ||
            degradationReasonCode.Length > 200 ||
            degradationReasonCode != degradationReasonCode.Trim())
        {
            return DeliveryFailureResult(
                "runtime.publication_reason",
                "Published degradation reason code is invalid.");
        }

        return Result.Success();
    }

    private static Result ValidatePublicationTransitions(
        RuntimeScopeId scopeId,
        ulong currentPosition,
        IReadOnlyList<CurrentEntry> transitions)
    {
        var expectedPosition = currentPosition;
        foreach (var transition in transitions)
        {
            if (transition.ScopeId != scopeId)
            {
                return DeliveryFailureResult(
                    "runtime.publication_scope",
                    "A current transition belongs to another runtime scope.");
            }

            expectedPosition = checked(expectedPosition + 1);
            if (transition.CurrentPosition.Value != expectedPosition)
            {
                return DeliveryFailureResult(
                    "runtime.publication_cursor",
                    "Published current transitions are not contiguous.");
            }
        }

        return Result.Success();
    }

    private static async Task<RuntimeProcessingDeliveryStage?> ReadDeliveryStageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        OwnerPosition<RuntimeSourceObligation> obligationPosition,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT stage
            FROM {CoreRuntimeMigrations.Schema}.processing_delivery
            WHERE scope_id = @scope_id AND obligation_position = @position
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue(
            "position",
            checked((long)obligationPosition.Value));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null
            ? null
            : (RuntimeProcessingDeliveryStage)Convert.ToInt16(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<DeliveryPublicationState?> ReadDeliveryPublicationStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        OwnerPosition<RuntimeSourceObligation> obligationPosition,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT stage, post_cut_acceptance::text
            FROM {CoreRuntimeMigrations.Schema}.processing_delivery
            WHERE scope_id = @scope_id AND obligation_position = @position
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue(
            "position",
            checked((long)obligationPosition.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new DeliveryPublicationState(
                (RuntimeProcessingDeliveryStage)reader.GetInt16(0),
                reader.IsDBNull(1) ? null : reader.GetString(1))
            : null;
    }

    private static async Task<PublishedScopeState?> ReadPublishedScopeStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT completed_obligation_position, current_position
            FROM {CoreRuntimeMigrations.Schema}.published_scope
            WHERE scope_id = @scope_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PublishedScopeState(reader.GetInt64(0), reader.GetInt64(1))
            : null;
    }

    private static async Task<bool> ReadProtectedContinuityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT protected_continuity
            FROM {CoreRuntimeMigrations.Schema}.scope_state
            WHERE scope_id = @scope_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    private static async Task UpsertPublishedTransitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CurrentEntry transition,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {CoreRuntimeMigrations.Schema}.published_current
                (scope_id, point_id, source_id, binding_generation, session_generation,
                 source_position, current_position, measurement_value, unit, quality, freshness,
                 source_timestamp, receive_timestamp, processed_timestamp)
            VALUES
                (@scope_id, @point_id, @source_id, @binding_generation, @session_generation,
                 @source_position, @current_position, @measurement_value, @unit, @quality, @freshness,
                 @source_timestamp, @receive_timestamp, @processed_timestamp)
            ON CONFLICT (scope_id, point_id) DO UPDATE
            SET source_id = EXCLUDED.source_id,
                binding_generation = EXCLUDED.binding_generation,
                session_generation = EXCLUDED.session_generation,
                source_position = EXCLUDED.source_position,
                current_position = EXCLUDED.current_position,
                measurement_value = EXCLUDED.measurement_value,
                unit = EXCLUDED.unit,
                quality = EXCLUDED.quality,
                freshness = EXCLUDED.freshness,
                source_timestamp = EXCLUDED.source_timestamp,
                receive_timestamp = EXCLUDED.receive_timestamp,
                processed_timestamp = EXCLUDED.processed_timestamp;

            INSERT INTO {CoreRuntimeMigrations.Schema}.published_delta
                (scope_id, current_position, point_id, source_id,
                 binding_generation, session_generation, source_position,
                 measurement_value, unit, quality, freshness,
                 source_timestamp, receive_timestamp, processed_timestamp)
            VALUES
                (@scope_id, @current_position, @point_id, @source_id,
                 @binding_generation, @session_generation, @source_position,
                 @measurement_value, @unit, @quality, @freshness,
                 @source_timestamp, @receive_timestamp, @processed_timestamp);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", transition.ScopeId.Value);
        command.Parameters.AddWithValue("point_id", transition.PointId.Value);
        command.Parameters.AddWithValue("source_id", transition.SourceId.Value);
        command.Parameters.AddWithValue(
            "binding_generation",
            checked((long)transition.BindingGeneration.Value));
        command.Parameters.AddWithValue(
            "session_generation",
            checked((long)transition.SessionGeneration.Value));
        command.Parameters.AddWithValue(
            "source_position",
            checked((long)transition.SourcePosition.Value));
        command.Parameters.AddWithValue(
            "current_position",
            checked((long)transition.CurrentPosition.Value));
        command.Parameters.AddWithValue(
            "measurement_value",
            NpgsqlDbType.Numeric,
            transition.Value.Value);
        command.Parameters.AddWithValue("unit", transition.Unit.Symbol);
        command.Parameters.AddWithValue("quality", (short)transition.Quality);
        command.Parameters.AddWithValue("freshness", (short)transition.Freshness);
        command.Parameters.AddWithValue("source_timestamp", transition.SourceTimestamp.Value);
        command.Parameters.AddWithValue("receive_timestamp", transition.ReceiveTimestamp.Value);
        command.Parameters.AddWithValue("processed_timestamp", transition.ProcessedTimestamp.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertPublishedScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        long completedObligationPosition,
        ulong currentPosition,
        bool protectedContinuity,
        bool ready,
        string? degradationReasonCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {CoreRuntimeMigrations.Schema}.published_scope
                (scope_id, completed_obligation_position, current_position,
                 earliest_delta_position, protected_continuity, ready,
                 degradation_reason_code, heartbeat_at, published_at,
                 measurement_semantic_version)
            VALUES
                (@scope_id, @completed_position, @current_position,
                 @earliest_delta_position, @protected_continuity, @ready,
                 @degradation_reason_code, @heartbeat_at, @published_at,
                 @measurement_semantic_version)
            ON CONFLICT (scope_id) DO UPDATE
            SET completed_obligation_position = EXCLUDED.completed_obligation_position,
                current_position = EXCLUDED.current_position,
                protected_continuity = EXCLUDED.protected_continuity,
                ready = EXCLUDED.ready,
                degradation_reason_code = EXCLUDED.degradation_reason_code,
                heartbeat_at = EXCLUDED.heartbeat_at,
                published_at = EXCLUDED.published_at,
                measurement_semantic_version = EXCLUDED.measurement_semantic_version;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("completed_position", completedObligationPosition);
        command.Parameters.AddWithValue("current_position", checked((long)currentPosition));
        command.Parameters.AddWithValue(
            "earliest_delta_position",
            checked((long)currentPosition + 1));
        command.Parameters.AddWithValue("protected_continuity", protectedContinuity);
        command.Parameters.AddWithValue("ready", ready);
        command.Parameters.AddWithValue(
            "degradation_reason_code",
            NpgsqlDbType.Text,
            (object?)degradationReasonCode ?? DBNull.Value);
        command.Parameters.AddWithValue("heartbeat_at", now);
        command.Parameters.AddWithValue("published_at", now);
        command.Parameters.AddWithValue(
            "measurement_semantic_version",
            (short)PublishedRuntimeReadiness.CurrentMeasurementSemanticVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PrunePublishedDeltaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        int retainedDeltaCapacity,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            DELETE FROM {CoreRuntimeMigrations.Schema}.published_delta
            WHERE scope_id = @scope_id
              AND current_position IN (
                  SELECT current_position
                  FROM {CoreRuntimeMigrations.Schema}.published_delta
                  WHERE scope_id = @scope_id
                  ORDER BY current_position DESC
                  OFFSET @retained_capacity
              );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("retained_capacity", retainedDeltaCapacity);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> CountRetainedDeltaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {CoreRuntimeMigrations.Schema}.published_delta WHERE scope_id = @scope_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        return checked((int)(long)(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L));
    }

    private static async Task<ulong> ReadEarliestDeltaPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        ulong currentPosition,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT COALESCE(min(current_position), @empty_position)
            FROM {CoreRuntimeMigrations.Schema}.published_delta
            WHERE scope_id = @scope_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("empty_position", checked((long)currentPosition + 1));
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        var value = (long)(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ??
            checked((long)currentPosition + 1));
        return checked((ulong)value);
    }

    private static Result<RuntimePublicationCommit> PublicationFailure(
        string code,
        string message) =>
        Result.Failure<RuntimePublicationCommit>(
            new OperationError(ErrorCode.From(code), message));

    private sealed record DeliveryPublicationState(
        RuntimeProcessingDeliveryStage Stage,
        string? PostCutAcceptance);

    private sealed record PublishedScopeState(
        long CompletedObligationPosition,
        long CurrentPosition);
}
