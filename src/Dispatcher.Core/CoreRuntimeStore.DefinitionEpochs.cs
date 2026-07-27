using Dispatcher.Semantics;
using Npgsql;

namespace Dispatcher.Core;

public sealed partial class CoreRuntimeStore
{
    public async Task<Result> EnsureDeliveryDefinitionEpochsAsync(
        RuntimeScopeId scopeId,
        OwnerPosition<RuntimeSourceObligation> obligationPosition,
        Guid configurationRevisionId,
        RevisionNumber alarmDefinitionEpoch,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(obligationPosition.Value);
        if (configurationRevisionId == Guid.Empty)
        {
            return DefinitionEpochFailure(
                "runtime.delivery_definition_epoch",
                "Configuration revision identity must be defined.");
        }

        if (!alarmDefinitionEpoch.IsDefined)
        {
            return DefinitionEpochFailure(
                "runtime.delivery_alarm_epoch",
                "Alarm definition epoch must be defined.");
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

        DeliveryDefinitionEpochRow? row;
        await using (var command = new NpgsqlCommand(
                         $"""
                         SELECT stage,
                                definition_epoch,
                                alarm_definition_epoch
                         FROM {CoreRuntimeMigrations.Schema}.processing_delivery
                         WHERE scope_id = @scope_id
                           AND obligation_position = @position
                         FOR UPDATE;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue(
                "position",
                checked((long)obligationPosition.Value));
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            row = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new DeliveryDefinitionEpochRow(
                    (RuntimeProcessingDeliveryStage)reader.GetInt16(0),
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.IsDBNull(2)
                        ? null
                        : RevisionNumber.From(
                            checked((ulong)reader.GetInt64(2))))
                : null;
        }

        if (row is null)
        {
            return DefinitionEpochFailure(
                "runtime.delivery_missing",
                "The processing delivery does not exist.");
        }

        if (row.Stage != RuntimeProcessingDeliveryStage.PendingDownstream)
        {
            return DefinitionEpochFailure(
                "runtime.delivery_downstream_closed",
                "The processing delivery no longer accepts definition epochs.");
        }

        if (row.ConfigurationRevisionId is { } storedConfiguration &&
            storedConfiguration != configurationRevisionId)
        {
            return DefinitionEpochFailure(
                "runtime.delivery_definition_epoch_conflict",
                "The processing delivery is already bound to another configuration revision.");
        }

        if (row.AlarmDefinitionEpoch is { } storedAlarm &&
            storedAlarm != alarmDefinitionEpoch)
        {
            return DefinitionEpochFailure(
                "runtime.delivery_alarm_epoch_conflict",
                "The processing delivery is already bound to another Alarm definition epoch.");
        }

        if (row.ConfigurationRevisionId is null ||
            row.AlarmDefinitionEpoch is null)
        {
            var now = clock.GetUtcNow();
            await using var command = new NpgsqlCommand(
                $"""
                UPDATE {CoreRuntimeMigrations.Schema}.processing_delivery
                SET definition_epoch = COALESCE(
                        definition_epoch,
                        @configuration_revision_id),
                    alarm_definition_epoch = COALESCE(
                        alarm_definition_epoch,
                        @alarm_definition_epoch),
                    updated_at = @updated_at
                WHERE scope_id = @scope_id
                  AND obligation_position = @position
                  AND stage = @pending_stage;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue(
                "configuration_revision_id",
                configurationRevisionId);
            command.Parameters.AddWithValue(
                "alarm_definition_epoch",
                checked((long)alarmDefinitionEpoch.Value));
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
                return DefinitionEpochFailure(
                    "runtime.delivery_state",
                    "The processing delivery changed while definition epochs were bound.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    private static Result DefinitionEpochFailure(
        string code,
        string message) =>
        Result.Failure(
            new OperationError(
                ErrorCode.From(code),
                message));

    private sealed record DeliveryDefinitionEpochRow(
        RuntimeProcessingDeliveryStage Stage,
        Guid? ConfigurationRevisionId,
        RevisionNumber? AlarmDefinitionEpoch);
}
