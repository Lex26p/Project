using System.Text.Json;
using System.Text.RegularExpressions;
using Dispatcher.Semantics;
using Npgsql;
using NpgsqlTypes;

namespace Dispatcher.Core;

public sealed partial class CoreRuntimeStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;

    public CoreRuntimeStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock)
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

    public Task<RuntimeSourceObligation> AppendCutAsync(
        RuntimeCut cut,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cut);
        var positions = cut.Observations.Select(item => item.SourcePosition.Value).ToArray();
        return AppendAsync(
            cut.Binding,
            cut.ScheduleSequence,
            RuntimeFactClass.SourceCut,
            positions.Length == 0 ? null : positions.Min(),
            positions.Length == 0 ? null : positions.Max(),
            JsonSerializer.Serialize(CutDto.From(cut)),
            null,
            cut,
            null,
            cancellationToken);
    }

    public Task<RuntimeSourceObligation> AppendGapAsync(
        RuntimeSourceGap gap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gap);
        return AppendAsync(
            gap.Binding,
            gap.ScheduleSequence,
            RuntimeFactClass.SourceGap,
            gap.FirstSourcePosition,
            gap.LastSourcePosition,
            null,
            gap.Reason,
            null,
            gap,
            cancellationToken);
    }

    public async Task<Result> SaveCheckpointAsync(
        CoreRuntimeCheckpoint checkpoint,
        ulong obligationPosition,
        bool protectedContinuity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, checkpoint.ScopeId, cancellationToken).ConfigureAwait(false);
        await EnsureScopeAsync(connection, transaction, checkpoint.ScopeId, cancellationToken).ConfigureAwait(false);
        long nextPosition;
        long currentCheckpoint;
        await using (var read = new NpgsqlCommand(
                         $"""
                         SELECT next_obligation_position, checkpoint_obligation_position
                         FROM {CoreRuntimeMigrations.Schema}.scope_state
                         WHERE scope_id = @scope_id FOR UPDATE;
                         """,
                         connection,
                         transaction))
        {
            read.Parameters.AddWithValue("scope_id", checkpoint.ScopeId.Value);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            nextPosition = reader.GetInt64(0);
            currentCheckpoint = reader.GetInt64(1);
        }

        var requested = checked((long)obligationPosition);
        if (requested < currentCheckpoint || requested >= nextPosition)
        {
            return Failure("runtime.checkpoint_position", "Checkpoint obligation position is invalid or regressed.");
        }

        var now = clock.GetUtcNow();
        await using (var update = new NpgsqlCommand(
                         $"""
                         UPDATE {CoreRuntimeMigrations.Schema}.scope_state
                         SET checkpoint_obligation_position = @position,
                             protected_continuity = @protected_continuity,
                             checkpoint = @checkpoint,
                             checkpointed_at = @checkpointed_at
                         WHERE scope_id = @scope_id;
                         UPDATE {CoreRuntimeMigrations.Schema}.source_obligation
                         SET checkpointed_at = COALESCE(checkpointed_at, @checkpointed_at)
                         WHERE scope_id = @scope_id AND obligation_position <= @position;
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("position", requested);
            update.Parameters.AddWithValue("protected_continuity", protectedContinuity);
            update.Parameters.AddWithValue(
                "checkpoint",
                NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(CheckpointDto.From(checkpoint)));
            update.Parameters.AddWithValue("checkpointed_at", now);
            update.Parameters.AddWithValue("scope_id", checkpoint.ScopeId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<RuntimeRecoveryState> LoadRecoveryAsync(
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        CoreRuntimeCheckpoint? checkpoint = null;
        ulong checkpointPosition = 0;
        var protectedContinuity = true;
        await using (var readState = new NpgsqlCommand(
                         $"""
                         SELECT checkpoint_obligation_position, protected_continuity, checkpoint::text
                         FROM {CoreRuntimeMigrations.Schema}.scope_state WHERE scope_id = @scope_id;
                         """,
                         connection,
                         transaction))
        {
            readState.Parameters.AddWithValue("scope_id", scopeId.Value);
            await using var reader = await readState.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                checkpointPosition = checked((ulong)reader.GetInt64(0));
                protectedContinuity = reader.GetBoolean(1);
                if (!reader.IsDBNull(2))
                {
                    checkpoint = JsonSerializer.Deserialize<CheckpointDto>(reader.GetString(2))!.ToModel();
                }
            }
        }

        var obligations = new List<RuntimeSourceObligation>();
        await using (var readObligations = CreateObligationQuery(
                         connection,
                         transaction,
                         "scope_id = @scope_id AND obligation_position > @position"))
        {
            readObligations.Parameters.AddWithValue("scope_id", scopeId.Value);
            readObligations.Parameters.AddWithValue("position", checked((long)checkpointPosition));
            await using var reader = await readObligations.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                obligations.Add(MapObligation(reader));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RuntimeRecoveryState(checkpoint, checkpointPosition, protectedContinuity, obligations);
    }

    public async Task<IReadOnlyList<RuntimeSourceObligation>> ReadGapsAsync(
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var obligations = new List<RuntimeSourceObligation>();
        await using var command = CreateObligationQuery(
            connection,
            transaction,
            "scope_id = @scope_id AND fact_class = 3");
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            obligations.Add(MapObligation(reader));
        }

        return obligations;
    }

    public async Task<long> CountPendingAsync(
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT count(*) FROM {CoreRuntimeMigrations.Schema}.source_obligation
            WHERE scope_id = @scope_id AND checkpointed_at IS NULL;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    private async Task<RuntimeSourceObligation> AppendAsync(
        SourceBinding binding,
        ulong scheduleSequence,
        RuntimeFactClass factClass,
        ulong? firstSourcePosition,
        ulong? lastSourcePosition,
        string? payload,
        string? gapReason,
        RuntimeCut? cut,
        RuntimeSourceGap? gap,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, binding.ScopeId, cancellationToken).ConfigureAwait(false);
        await EnsureScopeAsync(connection, transaction, binding.ScopeId, cancellationToken).ConfigureAwait(false);
        long position;
        await using (var read = new NpgsqlCommand(
                         $"""
                         SELECT next_obligation_position FROM {CoreRuntimeMigrations.Schema}.scope_state
                         WHERE scope_id = @scope_id FOR UPDATE;
                         """,
                         connection,
                         transaction))
        {
            read.Parameters.AddWithValue("scope_id", binding.ScopeId.Value);
            position = (long)(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        }

        var now = clock.GetUtcNow();
        await using (var insert = new NpgsqlCommand(
                         $"""
                         INSERT INTO {CoreRuntimeMigrations.Schema}.source_obligation
                             (scope_id, obligation_position, fact_class, protection, source_id,
                              binding_generation, session_generation, schedule_sequence,
                              first_source_position, last_source_position, payload, gap_reason, recorded_at)
                         VALUES
                             (@scope_id, @position, @fact_class, 2, @source_id,
                              @binding_generation, @session_generation, @schedule_sequence,
                              @first_source_position, @last_source_position, @payload, @gap_reason, @recorded_at);
                         UPDATE {CoreRuntimeMigrations.Schema}.scope_state
                         SET next_obligation_position = @next_position WHERE scope_id = @scope_id;
                         """,
                         connection,
                         transaction))
        {
            insert.Parameters.AddWithValue("scope_id", binding.ScopeId.Value);
            insert.Parameters.AddWithValue("position", position);
            insert.Parameters.AddWithValue("fact_class", (short)factClass);
            insert.Parameters.AddWithValue("source_id", binding.SourceId.Value);
            insert.Parameters.AddWithValue("binding_generation", checked((long)binding.BindingGeneration.Value));
            insert.Parameters.AddWithValue("session_generation", checked((long)binding.SessionGeneration.Value));
            insert.Parameters.AddWithValue("schedule_sequence", checked((long)scheduleSequence));
            insert.Parameters.AddWithValue(
                "first_source_position",
                firstSourcePosition is null ? DBNull.Value : checked((long)firstSourcePosition.Value));
            insert.Parameters.AddWithValue(
                "last_source_position",
                lastSourcePosition is null ? DBNull.Value : checked((long)lastSourcePosition.Value));
            insert.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, (object?)payload ?? DBNull.Value);
            insert.Parameters.AddWithValue("gap_reason", (object?)gapReason ?? DBNull.Value);
            insert.Parameters.AddWithValue("recorded_at", now);
            insert.Parameters.AddWithValue("next_position", checked(position + 1));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RuntimeSourceObligation(
            binding.ScopeId,
            new OwnerPosition<RuntimeSourceObligation>(checked((ulong)position)),
            factClass,
            RuntimeFactProtection.Protected,
            binding,
            scheduleSequence,
            cut,
            gap,
            now,
            null);
    }

    private static NpgsqlCommand CreateObligationQuery(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string predicate) => new(
        $"""
        SELECT scope_id, obligation_position, fact_class, source_id, binding_generation,
               session_generation, schedule_sequence, first_source_position, last_source_position,
               payload::text, gap_reason, recorded_at, checkpointed_at
        FROM {CoreRuntimeMigrations.Schema}.source_obligation
        WHERE {predicate} ORDER BY obligation_position;
        """,
        connection,
        transaction);

    private static RuntimeSourceObligation MapObligation(NpgsqlDataReader reader)
    {
        var scopeId = RuntimeScopeId.From(reader.GetGuid(0));
        var factClass = (RuntimeFactClass)reader.GetInt16(2);
        var binding = new SourceBinding(
            scopeId,
            SourceId.From(reader.GetGuid(3)),
            SourceBindingGeneration.From(checked((ulong)reader.GetInt64(4))),
            SourceSessionGeneration.From(checked((ulong)reader.GetInt64(5))));
        var scheduleSequence = checked((ulong)reader.GetInt64(6));
        RuntimeCut? cut = null;
        RuntimeSourceGap? gap = null;
        if (factClass == RuntimeFactClass.SourceCut)
        {
            cut = JsonSerializer.Deserialize<CutDto>(reader.GetString(9))!.ToModel(binding, scheduleSequence);
        }
        else
        {
            gap = new RuntimeSourceGap(
                binding,
                scheduleSequence,
                checked((ulong)reader.GetInt64(7)),
                checked((ulong)reader.GetInt64(8)),
                reader.GetString(10));
        }

        return new RuntimeSourceObligation(
            scopeId,
            new OwnerPosition<RuntimeSourceObligation>(checked((ulong)reader.GetInt64(1))),
            factClass,
            RuntimeFactProtection.Protected,
            binding,
            scheduleSequence,
            cut,
            gap,
            reader.GetFieldValue<DateTimeOffset>(11),
            reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12));
    }

    private static async Task EnsureScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {CoreRuntimeMigrations.Schema}.scope_state (scope_id) VALUES (@scope_id) ON CONFLICT DO NOTHING;",
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

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private sealed record ObservationDto(
        Guid PointId,
        ulong SourcePosition,
        long Value,
        string Unit,
        int Quality,
        int Freshness,
        DateTimeOffset SourceTimestamp)
    {
        public static ObservationDto From(SourceObservation observation) => new(
            observation.PointId.Value,
            observation.SourcePosition.Value,
            observation.Value.Value,
            observation.Unit.Symbol,
            (int)observation.Quality,
            (int)observation.Freshness,
            observation.SourceTimestamp.Value);

        public SourceObservation ToModel(SourceBinding binding) => new(
            binding.ScopeId,
            binding.SourceId,
            Dispatcher.Semantics.PointId.From(PointId),
            new OwnerPosition<SourceObservation>(SourcePosition),
            TypedValue.From(Value),
            Dispatcher.Semantics.Unit.FromSymbol(Unit),
            (DataQuality)Quality,
            (Dispatcher.Semantics.Freshness)Freshness,
            Dispatcher.Semantics.SourceTimestamp.FromUtc(SourceTimestamp));
    }

    private sealed record CutDto(ObservationDto[] Observations)
    {
        public static CutDto From(RuntimeCut cut) => new(cut.Observations.Select(ObservationDto.From).ToArray());

        public RuntimeCut ToModel(SourceBinding binding, ulong scheduleSequence) =>
            RuntimeCut.Normalize(binding, scheduleSequence, Observations.Select(item => item.ToModel(binding))).Value;
    }

    private sealed record BindingDto(Guid SourceId, ulong BindingGeneration, ulong SessionGeneration, ulong SourcePosition)
    {
        public static BindingDto From(SourceRuntimeCheckpoint source) => new(
            source.Binding.SourceId.Value,
            source.Binding.BindingGeneration.Value,
            source.Binding.SessionGeneration.Value,
            source.SourcePosition);

        public SourceRuntimeCheckpoint ToModel(RuntimeScopeId scopeId) => new(
            new SourceBinding(
                scopeId,
                Dispatcher.Core.SourceId.From(SourceId),
                SourceBindingGeneration.From(BindingGeneration),
                SourceSessionGeneration.From(SessionGeneration)),
            SourcePosition);
    }

    private sealed record CurrentDto(
        Guid SourceId,
        Guid PointId,
        ulong BindingGeneration,
        ulong SessionGeneration,
        ulong SourcePosition,
        ulong CurrentPosition,
        long Value,
        string Unit,
        int Quality,
        int Freshness,
        DateTimeOffset SourceTimestamp,
        DateTimeOffset ReceiveTimestamp,
        DateTimeOffset ProcessedTimestamp,
        long ProcessedMonotonicTimestamp)
    {
        public static CurrentDto From(CurrentEntry entry) => new(
            entry.SourceId.Value,
            entry.PointId.Value,
            entry.BindingGeneration.Value,
            entry.SessionGeneration.Value,
            entry.SourcePosition.Value,
            entry.CurrentPosition.Value,
            entry.Value.Value,
            entry.Unit.Symbol,
            (int)entry.Quality,
            (int)entry.Freshness,
            entry.SourceTimestamp.Value,
            entry.ReceiveTimestamp.Value,
            entry.ProcessedTimestamp.Value,
            entry.ProcessedMonotonicTimestamp.Value);

        public CurrentEntry ToModel(RuntimeScopeId scopeId) => new(
            scopeId,
            Dispatcher.Core.SourceId.From(SourceId),
            Dispatcher.Semantics.PointId.From(PointId),
            SourceBindingGeneration.From(BindingGeneration),
            SourceSessionGeneration.From(SessionGeneration),
            new OwnerPosition<SourceObservation>(SourcePosition),
            new OwnerPosition<CurrentEntry>(CurrentPosition),
            TypedValue.From(Value),
            Dispatcher.Semantics.Unit.FromSymbol(Unit),
            (DataQuality)Quality,
            (Dispatcher.Semantics.Freshness)Freshness,
            Dispatcher.Semantics.SourceTimestamp.FromUtc(SourceTimestamp),
            Dispatcher.Semantics.ReceiveTimestamp.FromUtc(ReceiveTimestamp),
            Dispatcher.Semantics.ProcessedTimestamp.FromUtc(ProcessedTimestamp),
            new MonotonicTimestamp(ProcessedMonotonicTimestamp));
    }

    private sealed record LivenessDto(
        Guid SourceId,
        ulong BindingGeneration,
        ulong SessionGeneration,
        ulong ScheduleSequence,
        ulong LastSourcePosition,
        int ObservationCount,
        ulong LivenessPosition,
        DateTimeOffset ReceiveTimestamp,
        DateTimeOffset ProcessedTimestamp,
        long ProcessedMonotonicTimestamp)
    {
        public static LivenessDto From(SourceLiveness item) => new(
            item.SourceId.Value,
            item.BindingGeneration.Value,
            item.SessionGeneration.Value,
            item.ScheduleSequence,
            item.LastSourcePosition,
            item.ObservationCount,
            item.LivenessPosition.Value,
            item.ReceiveTimestamp.Value,
            item.ProcessedTimestamp.Value,
            item.ProcessedMonotonicTimestamp.Value);

        public SourceLiveness ToModel(RuntimeScopeId scopeId) => new(
            scopeId,
            Dispatcher.Core.SourceId.From(SourceId),
            SourceBindingGeneration.From(BindingGeneration),
            SourceSessionGeneration.From(SessionGeneration),
            ScheduleSequence,
            LastSourcePosition,
            ObservationCount,
            new OwnerPosition<SourceLiveness>(LivenessPosition),
            Dispatcher.Semantics.ReceiveTimestamp.FromUtc(ReceiveTimestamp),
            Dispatcher.Semantics.ProcessedTimestamp.FromUtc(ProcessedTimestamp),
            new MonotonicTimestamp(ProcessedMonotonicTimestamp));
    }

    private sealed record CheckpointDto(
        Guid ScopeId,
        ulong CurrentPosition,
        ulong LivenessPosition,
        BindingDto[] Sources,
        CurrentDto[] Current,
        LivenessDto[] Liveness)
    {
        public static CheckpointDto From(CoreRuntimeCheckpoint checkpoint) => new(
            checkpoint.ScopeId.Value,
            checkpoint.CurrentPosition.Value,
            checkpoint.LivenessPosition.Value,
            checkpoint.Sources.Select(BindingDto.From).ToArray(),
            checkpoint.Current.Select(CurrentDto.From).ToArray(),
            checkpoint.Liveness.Select(LivenessDto.From).ToArray());

        public CoreRuntimeCheckpoint ToModel()
        {
            var scopeId = RuntimeScopeId.From(ScopeId);
            return new CoreRuntimeCheckpoint(
                scopeId,
                new OwnerPosition<CurrentEntry>(CurrentPosition),
                new OwnerPosition<SourceLiveness>(LivenessPosition),
                Sources.Select(item => item.ToModel(scopeId)).ToArray(),
                Current.Select(item => item.ToModel(scopeId)).ToArray(),
                Liveness.Select(item => item.ToModel(scopeId)).ToArray());
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
