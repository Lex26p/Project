using Npgsql;

namespace Dispatcher.Core;

public sealed record RuntimeDeliveryCleanupResult(
    int DeliveriesDeleted,
    int ObligationsDeleted,
    DateTimeOffset SafetyCutoff);

public sealed partial class CoreRuntimeStore
{
    public async Task<RuntimeDeliveryCleanupResult> CleanupCompletedDeliveriesAsync(
        RuntimeScopeId scopeId,
        TimeSpan safetyWindow,
        int maxDeleteCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            safetyWindow,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDeleteCount);

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

        var cutoff = clock.GetUtcNow() - safetyWindow;
        await using var command = new NpgsqlCommand(
            $"""
            WITH candidates AS (
                SELECT delivery.obligation_position
                FROM {CoreRuntimeMigrations.Schema}.processing_delivery AS delivery
                INNER JOIN {CoreRuntimeMigrations.Schema}.source_obligation AS obligation
                    ON obligation.scope_id = delivery.scope_id
                   AND obligation.obligation_position = delivery.obligation_position
                INNER JOIN {CoreRuntimeMigrations.Schema}.scope_state AS scope
                    ON scope.scope_id = delivery.scope_id
                WHERE delivery.scope_id = @scope_id
                  AND delivery.stage = @published_stage
                  AND delivery.published_at <= @cutoff
                  AND obligation.checkpointed_at IS NOT NULL
                  AND delivery.obligation_position <= scope.checkpoint_obligation_position
                ORDER BY delivery.obligation_position
                LIMIT @max_delete_count
                FOR UPDATE OF delivery SKIP LOCKED
            ),
            deleted_deliveries AS (
                DELETE FROM {CoreRuntimeMigrations.Schema}.processing_delivery AS delivery
                USING candidates
                WHERE delivery.scope_id = @scope_id
                  AND delivery.obligation_position = candidates.obligation_position
                RETURNING delivery.obligation_position
            ),
            deleted_obligations AS (
                DELETE FROM {CoreRuntimeMigrations.Schema}.source_obligation AS obligation
                USING deleted_deliveries
                WHERE obligation.scope_id = @scope_id
                  AND obligation.obligation_position = deleted_deliveries.obligation_position
                  AND obligation.checkpointed_at IS NOT NULL
                RETURNING obligation.obligation_position
            )
            SELECT
                (SELECT count(*) FROM deleted_deliveries),
                (SELECT count(*) FROM deleted_obligations);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue(
            "published_stage",
            (short)RuntimeProcessingDeliveryStage.Published);
        command.Parameters.AddWithValue("cutoff", cutoff);
        command.Parameters.AddWithValue("max_delete_count", maxDeleteCount);

        int deliveriesDeleted;
        int obligationsDeleted;
        await using (var reader = await command
                         .ExecuteReaderAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            deliveriesDeleted = checked((int)reader.GetInt64(0));
            obligationsDeleted = checked((int)reader.GetInt64(1));
        }
        if (deliveriesDeleted != obligationsDeleted)
        {
            throw new InvalidOperationException(
                "Completed delivery cleanup did not remove matching protected obligations.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RuntimeDeliveryCleanupResult(
            deliveriesDeleted,
            obligationsDeleted,
            cutoff);
    }
}
