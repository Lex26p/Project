using Dispatcher.Semantics;
using Npgsql;

namespace Dispatcher.Core;

public sealed partial class CoreRuntimeStore
{
    public async Task<Result<RuntimeDownstreamStageCommit>> CompleteDownstreamStageAsync(
        RuntimeScopeId scopeId,
        OwnerPosition<RuntimeSourceObligation> obligationPosition,
        RuntimeDownstreamStage downstreamStage,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(obligationPosition.Value);
        if (!Enum.IsDefined(downstreamStage))
        {
            throw new ArgumentOutOfRangeException(nameof(downstreamStage));
        }

        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await SetRoleAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(
            connection,
            transaction,
            scopeId,
            cancellationToken).ConfigureAwait(false);

        var progress = await ReadDownstreamProgressAsync(
            connection,
            transaction,
            scopeId,
            obligationPosition,
            cancellationToken).ConfigureAwait(false);
        if (progress is null)
        {
            return DownstreamFailure<RuntimeDownstreamStageCommit>(
                "runtime.delivery_missing",
                "The processing delivery does not exist.");
        }

        if (progress.Stage != RuntimeProcessingDeliveryStage.PendingDownstream)
        {
            if (ReadState(progress, downstreamStage) ==
                RuntimeDownstreamDeliveryState.Completed)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Result.Success(new RuntimeDownstreamStageCommit(
                    RuntimeDownstreamStageCommitStatus.AlreadyCompleted,
                    scopeId,
                    obligationPosition,
                    downstreamStage,
                    progress.UpdatedAt));
            }

            return DownstreamFailure<RuntimeDownstreamStageCommit>(
                "runtime.delivery_downstream_closed",
                "The processing delivery no longer accepts downstream stage updates.");
        }

        var currentState = ReadState(progress, downstreamStage);
        if (currentState == RuntimeDownstreamDeliveryState.Completed)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(new RuntimeDownstreamStageCommit(
                RuntimeDownstreamStageCommitStatus.AlreadyCompleted,
                scopeId,
                obligationPosition,
                downstreamStage,
                progress.UpdatedAt));
        }

        if (!PrerequisitesCompleted(progress, downstreamStage))
        {
            return DownstreamFailure<RuntimeDownstreamStageCommit>(
                "runtime.delivery_stage_order",
                "Downstream delivery stages must complete in History, Alarm, Event order.");
        }

        var now = clock.GetUtcNow();
        var stateColumn = StateColumn(downstreamStage);
        await using (var command = new NpgsqlCommand(
                         $"""
                         UPDATE {CoreRuntimeMigrations.Schema}.processing_delivery
                         SET {stateColumn} = @completed,
                             updated_at = @updated_at,
                             last_error_code = NULL,
                             last_error_at = NULL
                         WHERE scope_id = @scope_id
                           AND obligation_position = @position
                           AND stage = @pending_stage
                           AND {stateColumn} = @pending_state;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue(
                "completed",
                (short)RuntimeDownstreamDeliveryState.Completed);
            command.Parameters.AddWithValue("updated_at", now);
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue(
                "position",
                checked((long)obligationPosition.Value));
            command.Parameters.AddWithValue(
                "pending_stage",
                (short)RuntimeProcessingDeliveryStage.PendingDownstream);
            command.Parameters.AddWithValue(
                "pending_state",
                (short)RuntimeDownstreamDeliveryState.Pending);
            if (await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false) != 1)
            {
                return DownstreamFailure<RuntimeDownstreamStageCommit>(
                    "runtime.delivery_state",
                    "The processing delivery changed while downstream progress was committed.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new RuntimeDownstreamStageCommit(
            RuntimeDownstreamStageCommitStatus.Completed,
            scopeId,
            obligationPosition,
            downstreamStage,
            now));
    }

    public async Task<Result> RecordDownstreamFailureAsync(
        RuntimeScopeId scopeId,
        OwnerPosition<RuntimeSourceObligation> obligationPosition,
        RuntimeDownstreamStage downstreamStage,
        string errorCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(obligationPosition.Value);
        if (!Enum.IsDefined(downstreamStage))
        {
            throw new ArgumentOutOfRangeException(nameof(downstreamStage));
        }

        if (string.IsNullOrWhiteSpace(errorCode) ||
            errorCode.Length > 200 ||
            !string.Equals(errorCode, errorCode.Trim(), StringComparison.Ordinal))
        {
            return DownstreamFailure(
                "runtime.delivery_error_code",
                "A downstream failure code must be a trimmed non-empty value up to 200 characters.");
        }

        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await SetRoleAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(
            connection,
            transaction,
            scopeId,
            cancellationToken).ConfigureAwait(false);

        var progress = await ReadDownstreamProgressAsync(
            connection,
            transaction,
            scopeId,
            obligationPosition,
            cancellationToken).ConfigureAwait(false);
        if (progress is null)
        {
            return DownstreamFailure(
                "runtime.delivery_missing",
                "The processing delivery does not exist.");
        }

        if (progress.Stage != RuntimeProcessingDeliveryStage.PendingDownstream)
        {
            return DownstreamFailure(
                "runtime.delivery_downstream_closed",
                "The processing delivery no longer accepts downstream failure updates.");
        }

        if (ReadState(progress, downstreamStage) ==
            RuntimeDownstreamDeliveryState.Completed)
        {
            return DownstreamFailure(
                "runtime.delivery_stage_completed",
                "A completed downstream stage cannot record a new failure.");
        }

        if (!PrerequisitesCompleted(progress, downstreamStage))
        {
            return DownstreamFailure(
                "runtime.delivery_stage_order",
                "A failure can only be recorded for the next incomplete downstream stage.");
        }

        var now = clock.GetUtcNow();
        await using (var command = new NpgsqlCommand(
                         $"""
                         UPDATE {CoreRuntimeMigrations.Schema}.processing_delivery
                         SET last_error_code = @error_code,
                             last_error_at = @error_at,
                             updated_at = @updated_at
                         WHERE scope_id = @scope_id
                           AND obligation_position = @position
                           AND stage = @pending_stage;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("error_code", errorCode);
            command.Parameters.AddWithValue("error_at", now);
            command.Parameters.AddWithValue("updated_at", now);
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue(
                "position",
                checked((long)obligationPosition.Value));
            command.Parameters.AddWithValue(
                "pending_stage",
                (short)RuntimeProcessingDeliveryStage.PendingDownstream);
            if (await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false) != 1)
            {
                return DownstreamFailure(
                    "runtime.delivery_state",
                    "The processing delivery changed while downstream failure was committed.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    private static async Task<DownstreamProgressRow?> ReadDownstreamProgressAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        OwnerPosition<RuntimeSourceObligation> obligationPosition,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT stage,
                   history_state,
                   alarm_state,
                   event_state,
                   updated_at
            FROM {CoreRuntimeMigrations.Schema}.processing_delivery
            WHERE scope_id = @scope_id
              AND obligation_position = @position
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue(
            "position",
            checked((long)obligationPosition.Value));
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new DownstreamProgressRow(
                (RuntimeProcessingDeliveryStage)reader.GetInt16(0),
                (RuntimeDownstreamDeliveryState)reader.GetInt16(1),
                (RuntimeDownstreamDeliveryState)reader.GetInt16(2),
                (RuntimeDownstreamDeliveryState)reader.GetInt16(3),
                reader.GetFieldValue<DateTimeOffset>(4))
            : null;
    }

    private static RuntimeDownstreamDeliveryState ReadState(
        DownstreamProgressRow progress,
        RuntimeDownstreamStage downstreamStage) =>
        downstreamStage switch
        {
            RuntimeDownstreamStage.History => progress.HistoryState,
            RuntimeDownstreamStage.Alarm => progress.AlarmState,
            RuntimeDownstreamStage.Event => progress.EventState,
            _ => throw new ArgumentOutOfRangeException(nameof(downstreamStage)),
        };

    private static bool PrerequisitesCompleted(
        DownstreamProgressRow progress,
        RuntimeDownstreamStage downstreamStage) =>
        downstreamStage switch
        {
            RuntimeDownstreamStage.History => true,
            RuntimeDownstreamStage.Alarm =>
                progress.HistoryState == RuntimeDownstreamDeliveryState.Completed,
            RuntimeDownstreamStage.Event =>
                progress.HistoryState == RuntimeDownstreamDeliveryState.Completed &&
                progress.AlarmState == RuntimeDownstreamDeliveryState.Completed,
            _ => throw new ArgumentOutOfRangeException(nameof(downstreamStage)),
        };

    private static string StateColumn(RuntimeDownstreamStage downstreamStage) =>
        downstreamStage switch
        {
            RuntimeDownstreamStage.History => "history_state",
            RuntimeDownstreamStage.Alarm => "alarm_state",
            RuntimeDownstreamStage.Event => "event_state",
            _ => throw new ArgumentOutOfRangeException(nameof(downstreamStage)),
        };

    private static Result<T> DownstreamFailure<T>(
        string code,
        string message) =>
        Result.Failure<T>(
            new OperationError(
                ErrorCode.From(code),
                message));

    private static Result DownstreamFailure(
        string code,
        string message) =>
        Result.Failure(
            new OperationError(
                ErrorCode.From(code),
                message));

    private sealed record DownstreamProgressRow(
        RuntimeProcessingDeliveryStage Stage,
        RuntimeDownstreamDeliveryState HistoryState,
        RuntimeDownstreamDeliveryState AlarmState,
        RuntimeDownstreamDeliveryState EventState,
        DateTimeOffset UpdatedAt);
}
