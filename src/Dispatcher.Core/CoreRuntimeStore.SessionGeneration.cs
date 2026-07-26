using Dispatcher.Semantics;
using Npgsql;

namespace Dispatcher.Core;

public sealed partial class CoreRuntimeStore
{
    public async Task<SourceSessionGeneration> AllocateSourceSessionGenerationAsync(
        RuntimeScopeId scopeId,
        SourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        _ = scopeId.Value;
        _ = sourceId.Value;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        await EnsureScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {CoreRuntimeMigrations.Schema}.source_session_generation
                (scope_id, source_id, last_generation, allocated_at)
            VALUES
                (@scope_id, @source_id, 1, @allocated_at)
            ON CONFLICT (scope_id, source_id) DO UPDATE
            SET last_generation = {CoreRuntimeMigrations.Schema}.source_session_generation.last_generation + 1,
                allocated_at = EXCLUDED.allocated_at
            RETURNING last_generation;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("source_id", sourceId.Value);
        command.Parameters.AddWithValue("allocated_at", clock.GetUtcNow());
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (scalar is not long generation || generation <= 0)
        {
            throw new InvalidOperationException("PostgreSQL did not return a valid source session generation.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return SourceSessionGeneration.From(checked((ulong)generation));
    }
}
