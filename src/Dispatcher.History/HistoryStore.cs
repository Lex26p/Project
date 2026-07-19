using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dispatcher.Core;
using Dispatcher.Semantics;
using Npgsql;

namespace Dispatcher.History;

public sealed partial class HistoryStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;
    private readonly HistoryIngestCommitHook? commitHook;

    public HistoryStore(
        NpgsqlDataSource dataSource,
        string databaseRole,
        IWallClock clock,
        HistoryIngestCommitHook? commitHook = null)
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
        this.commitHook = commitHook;
    }

    public async Task<Result<HistoryIngestAcceptance>> AcceptAsync(
        RuntimeSourceObligation obligation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(obligation);
        var validation = Validate(obligation);
        if (validation.IsFailure)
        {
            return Result.Failure<HistoryIngestAcceptance>(validation.Error!);
        }

        var runtimePosition = new RuntimeFactPosition(obligation.Position.Value);
        var fingerprint = ComputeFingerprint(obligation);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, obligation.ScopeId, cancellationToken).ConfigureAwait(false);
        await EnsureScopeAsync(connection, transaction, obligation.ScopeId, cancellationToken).ConfigureAwait(false);

        var existing = await ReadExistingBatchAsync(
            connection,
            transaction,
            obligation.ScopeId,
            runtimePosition,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.FactClass != obligation.FactClass ||
                !string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return Failure<HistoryIngestAcceptance>(
                    "history.ingest_conflict",
                    "Runtime fact position was already accepted with different content.");
            }

            var duplicate = await LoadAcceptanceAsync(
                connection,
                transaction,
                obligation.ScopeId,
                runtimePosition,
                HistoryIngestStatus.Duplicate,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(duplicate);
        }

        var nextStreamPosition = await ReadNextStreamPositionAsync(
            connection,
            transaction,
            obligation.ScopeId,
            cancellationToken).ConfigureAwait(false);
        var recordCount = obligation.FactClass == RuntimeFactClass.SourceCut
            ? obligation.Cut!.Observations.Count
            : 1;
        var firstStreamPosition = new HistoryStreamPosition(checked((ulong)nextStreamPosition));
        var lastStreamPosition = new HistoryStreamPosition(
            checked(firstStreamPosition.Value + (ulong)recordCount - 1));
        var acceptedAt = clock.GetUtcNow();

        await InsertBatchAsync(
            connection,
            transaction,
            obligation,
            runtimePosition,
            fingerprint,
            firstStreamPosition,
            lastStreamPosition,
            acceptedAt,
            cancellationToken).ConfigureAwait(false);

        var samples = new List<HistorySample>();
        HistoryGap? gap = null;
        if (obligation.FactClass == RuntimeFactClass.SourceCut)
        {
            var streamPosition = firstStreamPosition;
            foreach (var observation in obligation.Cut!.Observations)
            {
                var sample = await InsertSampleAsync(
                    connection,
                    transaction,
                    obligation.ScopeId,
                    runtimePosition,
                    streamPosition,
                    observation,
                    acceptedAt,
                    cancellationToken).ConfigureAwait(false);
                samples.Add(sample);
                streamPosition = streamPosition.Next();
            }
        }
        else
        {
            gap = await InsertGapAsync(
                connection,
                transaction,
                obligation.ScopeId,
                runtimePosition,
                firstStreamPosition,
                obligation.Gap!,
                acceptedAt,
                cancellationToken).ConfigureAwait(false);
        }

        await UpdateNextStreamPositionAsync(
            connection,
            transaction,
            obligation.ScopeId,
            checked((long)lastStreamPosition.Value + 1),
            cancellationToken).ConfigureAwait(false);
        var checkpoint = await AdvanceCheckpointAsync(
            connection,
            transaction,
            obligation.ScopeId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var acceptance = new HistoryIngestAcceptance(
            HistoryIngestStatus.Accepted,
            runtimePosition,
            samples,
            gap,
            checkpoint);
        if (commitHook is not null)
        {
            await commitHook.AfterCommittedAsync(acceptance, cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(acceptance);
    }

    public async Task<HistoryRecoveryCheckpoint> LoadRecoveryCheckpointAsync(
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var checkpoint = await ReadCheckpointAsync(
            connection,
            transaction,
            scopeId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    private static Result Validate(RuntimeSourceObligation obligation)
    {
        if (obligation.Position.Value == 0 ||
            obligation.Protection != RuntimeFactProtection.Protected ||
            obligation.Binding.ScopeId != obligation.ScopeId)
        {
            return Failure("history.fact_invalid", "Runtime fact identity is invalid.");
        }

        if (obligation.FactClass == RuntimeFactClass.SourceCut && obligation.Cut is not null &&
            obligation.Gap is null && obligation.Cut.Observations.Count > 0 &&
            obligation.Cut.Binding == obligation.Binding &&
            obligation.Cut.ScheduleSequence == obligation.ScheduleSequence)
        {
            return Result.Success();
        }

        if (obligation.FactClass == RuntimeFactClass.SourceGap && obligation.Gap is not null &&
            obligation.Cut is null && obligation.Gap.Binding == obligation.Binding &&
            obligation.Gap.ScheduleSequence == obligation.ScheduleSequence)
        {
            return Result.Success();
        }

        return Failure("history.fact_invalid", "History accepts only non-empty RuntimeCut or RuntimeSourceGap facts.");
    }

    private static async Task InsertBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeSourceObligation obligation,
        RuntimeFactPosition runtimePosition,
        string fingerprint,
        HistoryStreamPosition firstStreamPosition,
        HistoryStreamPosition lastStreamPosition,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {HistoryMigrations.Schema}.ingest_batch
                (scope_id, runtime_fact_position, fact_class, fingerprint,
                 first_stream_position, last_stream_position, accepted_at)
            VALUES
                (@scope_id, @runtime_position, @fact_class, @fingerprint,
                 @first_stream_position, @last_stream_position, @accepted_at);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", obligation.ScopeId.Value);
        command.Parameters.AddWithValue("runtime_position", checked((long)runtimePosition.Value));
        command.Parameters.AddWithValue("fact_class", (short)obligation.FactClass);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        command.Parameters.AddWithValue("first_stream_position", checked((long)firstStreamPosition.Value));
        command.Parameters.AddWithValue("last_stream_position", checked((long)lastStreamPosition.Value));
        command.Parameters.AddWithValue("accepted_at", acceptedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HistorySample> InsertSampleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        RuntimeFactPosition runtimePosition,
        HistoryStreamPosition streamPosition,
        SourceObservation observation,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken)
    {
        var watermark = await ReadWatermarkAsync(
            connection,
            transaction,
            scopeId,
            observation.SourceId,
            observation.PointId,
            cancellationToken).ConfigureAwait(false);
        var isLate = watermark is not null && observation.SourceTimestamp.Value < watermark.MaxSourceTimestamp;
        var isOutOfOrder = watermark is not null &&
            (observation.SourcePosition.Value <= watermark.MaxSourcePosition ||
             runtimePosition.Value < watermark.LastRuntimeFactPosition);

        await using (var insert = new NpgsqlCommand(
                         $"""
                         INSERT INTO {HistoryMigrations.Schema}.sample
                             (scope_id, history_stream_position, runtime_fact_position, source_id, point_id,
                              source_position, value, unit, quality, freshness, source_timestamp,
                              accepted_at, is_late, is_out_of_order)
                         VALUES
                             (@scope_id, @stream_position, @runtime_position, @source_id, @point_id,
                              @source_position, @value, @unit, @quality, @freshness, @source_timestamp,
                              @accepted_at, @is_late, @is_out_of_order);
                         """,
                         connection,
                         transaction))
        {
            insert.Parameters.AddWithValue("scope_id", scopeId.Value);
            insert.Parameters.AddWithValue("stream_position", checked((long)streamPosition.Value));
            insert.Parameters.AddWithValue("runtime_position", checked((long)runtimePosition.Value));
            insert.Parameters.AddWithValue("source_id", observation.SourceId.Value);
            insert.Parameters.AddWithValue("point_id", observation.PointId.Value);
            insert.Parameters.AddWithValue("source_position", checked((long)observation.SourcePosition.Value));
            insert.Parameters.AddWithValue("value", observation.Value.Value);
            insert.Parameters.AddWithValue("unit", observation.Unit.Symbol);
            insert.Parameters.AddWithValue("quality", (short)observation.Quality);
            insert.Parameters.AddWithValue("freshness", (short)observation.Freshness);
            insert.Parameters.AddWithValue("source_timestamp", observation.SourceTimestamp.Value);
            insert.Parameters.AddWithValue("accepted_at", acceptedAt);
            insert.Parameters.AddWithValue("is_late", isLate);
            insert.Parameters.AddWithValue("is_out_of_order", isOutOfOrder);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await UpsertWatermarkAsync(
            connection,
            transaction,
            scopeId,
            runtimePosition,
            observation,
            cancellationToken).ConfigureAwait(false);
        return new HistorySample(
            scopeId,
            streamPosition,
            runtimePosition,
            observation.SourceId,
            observation.PointId,
            observation.SourcePosition,
            observation.Value,
            observation.Unit,
            observation.Quality,
            observation.Freshness,
            observation.SourceTimestamp,
            acceptedAt,
            isLate,
            isOutOfOrder);
    }

    private static async Task<HistoryGap> InsertGapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        RuntimeFactPosition runtimePosition,
        HistoryStreamPosition streamPosition,
        RuntimeSourceGap gap,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {HistoryMigrations.Schema}.gap
                (scope_id, history_stream_position, runtime_fact_position, source_id,
                 first_source_position, last_source_position, reason, accepted_at)
            VALUES
                (@scope_id, @stream_position, @runtime_position, @source_id,
                 @first_source_position, @last_source_position, @reason, @accepted_at);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("stream_position", checked((long)streamPosition.Value));
        command.Parameters.AddWithValue("runtime_position", checked((long)runtimePosition.Value));
        command.Parameters.AddWithValue("source_id", gap.Binding.SourceId.Value);
        command.Parameters.AddWithValue("first_source_position", checked((long)gap.FirstSourcePosition));
        command.Parameters.AddWithValue("last_source_position", checked((long)gap.LastSourcePosition));
        command.Parameters.AddWithValue("reason", gap.Reason);
        command.Parameters.AddWithValue("accepted_at", acceptedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new HistoryGap(
            scopeId,
            streamPosition,
            runtimePosition,
            gap.Binding.SourceId,
            gap.FirstSourcePosition,
            gap.LastSourcePosition,
            gap.Reason,
            acceptedAt);
    }

    private static async Task<HistoryRecoveryCheckpoint> AdvanceCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        var checkpoint = await ReadCheckpointAsync(
            connection,
            transaction,
            scopeId,
            cancellationToken).ConfigureAwait(false);
        var runtimePosition = checkpoint.RuntimeFactPosition.Value;
        var streamPosition = checkpoint.HistoryStreamPosition.Value;
        while (true)
        {
            await using var command = new NpgsqlCommand(
                $"""
                SELECT last_stream_position FROM {HistoryMigrations.Schema}.ingest_batch
                WHERE scope_id = @scope_id AND runtime_fact_position = @runtime_position;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("runtime_position", checked((long)runtimePosition + 1));
            var next = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                break;
            }

            runtimePosition = checked(runtimePosition + 1);
            streamPosition = Math.Max(streamPosition, checked((ulong)(long)next));
        }

        await using (var update = new NpgsqlCommand(
                         $"""
                         UPDATE {HistoryMigrations.Schema}.scope_state
                         SET checkpoint_runtime_fact_position = @runtime_position,
                             checkpoint_stream_position = @stream_position
                         WHERE scope_id = @scope_id;
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("scope_id", scopeId.Value);
            update.Parameters.AddWithValue("runtime_position", checked((long)runtimePosition));
            update.Parameters.AddWithValue("stream_position", checked((long)streamPosition));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new HistoryRecoveryCheckpoint(
            scopeId,
            new RuntimeFactPosition(runtimePosition),
            new HistoryStreamPosition(streamPosition));
    }

    private static async Task<HistoryRecoveryCheckpoint> ReadCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT checkpoint_runtime_fact_position, checkpoint_stream_position
            FROM {HistoryMigrations.Schema}.scope_state WHERE scope_id = @scope_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new HistoryRecoveryCheckpoint(
                scopeId,
                new RuntimeFactPosition(checked((ulong)reader.GetInt64(0))),
                new HistoryStreamPosition(checked((ulong)reader.GetInt64(1))))
            : new HistoryRecoveryCheckpoint(scopeId, new RuntimeFactPosition(0), new HistoryStreamPosition(0));
    }

    private static async Task<HistoryIngestAcceptance> LoadAcceptanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        RuntimeFactPosition runtimePosition,
        HistoryIngestStatus status,
        CancellationToken cancellationToken)
    {
        var samples = await ReadSamplesAsync(
            connection,
            transaction,
            scopeId,
            runtimePosition,
            cancellationToken).ConfigureAwait(false);
        var gaps = await ReadGapsAsync(
            connection,
            transaction,
            scopeId,
            runtimePosition,
            cancellationToken).ConfigureAwait(false);
        var checkpoint = await ReadCheckpointAsync(
            connection,
            transaction,
            scopeId,
            cancellationToken).ConfigureAwait(false);
        return new HistoryIngestAcceptance(
            status,
            runtimePosition,
            samples,
            gaps.SingleOrDefault(),
            checkpoint);
    }

    private static async Task<List<HistorySample>> ReadSamplesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        RuntimeFactPosition? runtimePosition,
        CancellationToken cancellationToken)
    {
        var predicate = runtimePosition is null ? string.Empty : " AND runtime_fact_position = @runtime_position";
        await using var command = new NpgsqlCommand(
            $"""
            SELECT history_stream_position, runtime_fact_position, source_id, point_id,
                   source_position, value, unit, quality, freshness, source_timestamp,
                   accepted_at, is_late, is_out_of_order
            FROM {HistoryMigrations.Schema}.sample
            WHERE scope_id = @scope_id{predicate}
            ORDER BY history_stream_position;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        if (runtimePosition is not null)
        {
            command.Parameters.AddWithValue("runtime_position", checked((long)runtimePosition.Value.Value));
        }

        var samples = new List<HistorySample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            samples.Add(new HistorySample(
                scopeId,
                new HistoryStreamPosition(checked((ulong)reader.GetInt64(0))),
                new RuntimeFactPosition(checked((ulong)reader.GetInt64(1))),
                SourceId.From(reader.GetGuid(2)),
                PointId.From(reader.GetGuid(3)),
                new OwnerPosition<SourceObservation>(checked((ulong)reader.GetInt64(4))),
                TypedValue.From(reader.GetInt64(5)),
                Unit.FromSymbol(reader.GetString(6)),
                (DataQuality)reader.GetInt16(7),
                (Freshness)reader.GetInt16(8),
                SourceTimestamp.FromUtc(reader.GetFieldValue<DateTimeOffset>(9)),
                reader.GetFieldValue<DateTimeOffset>(10),
                reader.GetBoolean(11),
                reader.GetBoolean(12)));
        }

        return samples;
    }

    private static async Task<List<HistoryGap>> ReadGapsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        RuntimeFactPosition? runtimePosition,
        CancellationToken cancellationToken)
    {
        var predicate = runtimePosition is null ? string.Empty : " AND runtime_fact_position = @runtime_position";
        await using var command = new NpgsqlCommand(
            $"""
            SELECT history_stream_position, runtime_fact_position, source_id,
                   first_source_position, last_source_position, reason, accepted_at
            FROM {HistoryMigrations.Schema}.gap
            WHERE scope_id = @scope_id{predicate}
            ORDER BY history_stream_position;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        if (runtimePosition is not null)
        {
            command.Parameters.AddWithValue("runtime_position", checked((long)runtimePosition.Value.Value));
        }

        var gaps = new List<HistoryGap>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            gaps.Add(new HistoryGap(
                scopeId,
                new HistoryStreamPosition(checked((ulong)reader.GetInt64(0))),
                new RuntimeFactPosition(checked((ulong)reader.GetInt64(1))),
                SourceId.From(reader.GetGuid(2)),
                checked((ulong)reader.GetInt64(3)),
                checked((ulong)reader.GetInt64(4)),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return gaps;
    }

    private static async Task<Watermark?> ReadWatermarkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        SourceId sourceId,
        PointId pointId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT max_source_position, max_source_timestamp, last_runtime_fact_position
            FROM {HistoryMigrations.Schema}.source_watermark
            WHERE scope_id = @scope_id AND source_id = @source_id AND point_id = @point_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("source_id", sourceId.Value);
        command.Parameters.AddWithValue("point_id", pointId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new Watermark(
                checked((ulong)reader.GetInt64(0)),
                reader.GetFieldValue<DateTimeOffset>(1),
                checked((ulong)reader.GetInt64(2)))
            : null;
    }

    private static async Task UpsertWatermarkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        RuntimeFactPosition runtimePosition,
        SourceObservation observation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {HistoryMigrations.Schema}.source_watermark
                (scope_id, source_id, point_id, max_source_position,
                 max_source_timestamp, last_runtime_fact_position)
            VALUES
                (@scope_id, @source_id, @point_id, @source_position,
                 @source_timestamp, @runtime_position)
            ON CONFLICT (scope_id, source_id, point_id) DO UPDATE
            SET max_source_position = GREATEST(
                    {HistoryMigrations.Schema}.source_watermark.max_source_position,
                    EXCLUDED.max_source_position),
                max_source_timestamp = GREATEST(
                    {HistoryMigrations.Schema}.source_watermark.max_source_timestamp,
                    EXCLUDED.max_source_timestamp),
                last_runtime_fact_position = GREATEST(
                    {HistoryMigrations.Schema}.source_watermark.last_runtime_fact_position,
                    EXCLUDED.last_runtime_fact_position);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("source_id", observation.SourceId.Value);
        command.Parameters.AddWithValue("point_id", observation.PointId.Value);
        command.Parameters.AddWithValue("source_position", checked((long)observation.SourcePosition.Value));
        command.Parameters.AddWithValue("source_timestamp", observation.SourceTimestamp.Value);
        command.Parameters.AddWithValue("runtime_position", checked((long)runtimePosition.Value));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ExistingBatch?> ReadExistingBatchAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        RuntimeFactPosition runtimePosition,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT fact_class, fingerprint FROM {HistoryMigrations.Schema}.ingest_batch
            WHERE scope_id = @scope_id AND runtime_fact_position = @runtime_position;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("runtime_position", checked((long)runtimePosition.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ExistingBatch((RuntimeFactClass)reader.GetInt16(0), reader.GetString(1))
            : null;
    }

    private static async Task<long> ReadNextStreamPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT next_stream_position FROM {HistoryMigrations.Schema}.scope_state
            WHERE scope_id = @scope_id FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    private static async Task UpdateNextStreamPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        long nextStreamPosition,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {HistoryMigrations.Schema}.scope_state
            SET next_stream_position = @next_stream_position WHERE scope_id = @scope_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("next_stream_position", nextStreamPosition);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {HistoryMigrations.Schema}.scope_state (scope_id) VALUES (@scope_id) ON CONFLICT DO NOTHING;",
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
        command.Parameters.AddWithValue("scope_id", $"history:{scopeId.Value:N}");
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

    private static string ComputeFingerprint(RuntimeSourceObligation obligation)
    {
        var dto = obligation.FactClass == RuntimeFactClass.SourceCut
            ? FactFingerprint.From(obligation, obligation.Cut!)
            : FactFingerprint.From(obligation, obligation.Gap!);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dto));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));

    private sealed record ExistingBatch(RuntimeFactClass FactClass, string Fingerprint);

    private sealed record Watermark(
        ulong MaxSourcePosition,
        DateTimeOffset MaxSourceTimestamp,
        ulong LastRuntimeFactPosition);

    private sealed record ObservationFingerprint(
        Guid PointId,
        ulong SourcePosition,
        long Value,
        string Unit,
        int Quality,
        int Freshness,
        DateTimeOffset SourceTimestamp)
    {
        public static ObservationFingerprint From(SourceObservation observation) => new(
            observation.PointId.Value,
            observation.SourcePosition.Value,
            observation.Value.Value,
            observation.Unit.Symbol,
            (int)observation.Quality,
            (int)observation.Freshness,
            observation.SourceTimestamp.Value);
    }

    private sealed record FactFingerprint(
        Guid ScopeId,
        Guid SourceId,
        ulong BindingGeneration,
        ulong SessionGeneration,
        ulong ScheduleSequence,
        int FactClass,
        ObservationFingerprint[]? Observations,
        ulong? FirstSourcePosition,
        ulong? LastSourcePosition,
        string? GapReason)
    {
        public static FactFingerprint From(RuntimeSourceObligation obligation, RuntimeCut cut) => new(
            obligation.ScopeId.Value,
            obligation.Binding.SourceId.Value,
            obligation.Binding.BindingGeneration.Value,
            obligation.Binding.SessionGeneration.Value,
            obligation.ScheduleSequence,
            (int)obligation.FactClass,
            cut.Observations.Select(ObservationFingerprint.From).ToArray(),
            null,
            null,
            null);

        public static FactFingerprint From(RuntimeSourceObligation obligation, RuntimeSourceGap gap) => new(
            obligation.ScopeId.Value,
            obligation.Binding.SourceId.Value,
            obligation.Binding.BindingGeneration.Value,
            obligation.Binding.SessionGeneration.Value,
            obligation.ScheduleSequence,
            (int)obligation.FactClass,
            null,
            gap.FirstSourcePosition,
            gap.LastSourcePosition,
            gap.Reason);
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
