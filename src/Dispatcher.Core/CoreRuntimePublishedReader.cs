using System.Data;
using System.Text.RegularExpressions;
using Dispatcher.Semantics;
using Npgsql;

namespace Dispatcher.Core;

public sealed partial class CoreRuntimePublishedReader
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly PublishedCurrentReadLimits limits;

    public CoreRuntimePublishedReader(
        NpgsqlDataSource dataSource,
        string databaseRole,
        PublishedCurrentReadLimits limits)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        ArgumentNullException.ThrowIfNull(limits);
        if (!RolePattern().IsMatch(databaseRole))
        {
            throw new ArgumentException(
                "Invalid PostgreSQL role name.",
                nameof(databaseRole));
        }

        this.dataSource = dataSource;
        this.databaseRole = databaseRole;
        this.limits = limits;
    }

    public async Task<PublishedRuntimeReadiness> ReadReadinessAsync(
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken)
            .ConfigureAwait(false);
        await SetRoleAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        var readiness = await ReadReadinessCoreAsync(
            connection,
            transaction,
            scopeId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return readiness;
    }

    public async Task<PublishedCurrentSnapshot> ReadSnapshotAsync(
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken)
            .ConfigureAwait(false);
        await SetRoleAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);

        var readiness = await ReadReadinessCoreAsync(
            connection,
            transaction,
            scopeId,
            cancellationToken).ConfigureAwait(false);
        var entries = readiness.Published
            ? await ReadEntriesAsync(
                connection,
                transaction,
                scopeId,
                tableName: "published_current",
                predicate: string.Empty,
                cursor: null,
                limits.MaxSnapshotPoints,
                cancellationToken).ConfigureAwait(false)
            : [];
        if (entries.Count > limits.MaxSnapshotPoints)
        {
            throw new PublishedCurrentReadLimitExceededException();
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PublishedCurrentSnapshot(
            readiness,
            entries.Take(limits.MaxSnapshotPoints).ToArray());
    }

    public async Task<PublishedCurrentDelta> ReadDeltaAsync(
        RuntimeScopeId scopeId,
        ConsumerCursor<PublishedCurrentEntry> cursor,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken)
            .ConfigureAwait(false);
        await SetRoleAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);

        var readiness = await ReadReadinessCoreAsync(
            connection,
            transaction,
            scopeId,
            cancellationToken).ConfigureAwait(false);
        PublishedCurrentDeltaStatus status;
        IReadOnlyList<PublishedCurrentEntry> changes = [];

        if (!readiness.Published)
        {
            status = PublishedCurrentDeltaStatus.ScopeNotPublished;
        }
        else if (cursor.Value > readiness.CurrentCursor.Value)
        {
            status = PublishedCurrentDeltaStatus.CursorAhead;
        }
        else if (cursor.Value < readiness.EarliestResumableCursor.Value)
        {
            status = PublishedCurrentDeltaStatus.CursorTooOld;
        }
        else
        {
            status = PublishedCurrentDeltaStatus.Available;
            changes = await ReadEntriesAsync(
                connection,
                transaction,
                scopeId,
                tableName: "published_delta",
                predicate: "AND current_position > @cursor",
                cursor,
                limits.MaxDeltaChanges,
                cancellationToken).ConfigureAwait(false);
        }

        var hasMore = changes.Count > limits.MaxDeltaChanges;
        if (hasMore)
        {
            changes = changes.Take(limits.MaxDeltaChanges).ToArray();
        }
        var to = hasMore
            ? new ConsumerCursor<PublishedCurrentEntry>(
                changes[^1].CurrentPosition.Value)
            : readiness.CurrentCursor;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PublishedCurrentDelta(
            readiness,
            cursor,
            to,
            status,
            changes);
    }

    private static async Task<PublishedRuntimeReadiness> ReadReadinessCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT completed_obligation_position,
                   current_position,
                   earliest_delta_position,
                   protected_continuity,
                   ready,
                   degradation_reason_code,
                   heartbeat_at,
                   published_at
            FROM {CoreRuntimeMigrations.Schema}.published_scope
            WHERE scope_id = @scope_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new PublishedRuntimeReadiness(
                scopeId,
                Published: false,
                new OwnerPosition<RuntimeSourceObligation>(0),
                new ConsumerCursor<PublishedCurrentEntry>(0),
                new ConsumerCursor<PublishedCurrentEntry>(0),
                ProtectedContinuity: false,
                Ready: false,
                DegradationReasonCode: null,
                HeartbeatAt: null,
                PublishedAt: null);
        }

        var currentPosition = checked((ulong)reader.GetInt64(1));
        var earliestDeltaPosition = checked((ulong)reader.GetInt64(2));
        var earliestResumableCursor = earliestDeltaPosition == 0
            ? 0
            : checked(earliestDeltaPosition - 1);
        return new PublishedRuntimeReadiness(
            scopeId,
            Published: true,
            new OwnerPosition<RuntimeSourceObligation>(
                checked((ulong)reader.GetInt64(0))),
            new ConsumerCursor<PublishedCurrentEntry>(currentPosition),
            new ConsumerCursor<PublishedCurrentEntry>(
                earliestResumableCursor),
            reader.GetBoolean(3),
            reader.GetBoolean(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(7));
    }

    private static async Task<IReadOnlyList<PublishedCurrentEntry>> ReadEntriesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        string tableName,
        string predicate,
        ConsumerCursor<PublishedCurrentEntry>? cursor,
        int entryLimit,
        CancellationToken cancellationToken)
    {
        var entries = new List<PublishedCurrentEntry>();
        await using var command = new NpgsqlCommand(
            $"""
            SELECT source_id,
                   point_id,
                   binding_generation,
                   session_generation,
                   source_position,
                   current_position,
                   value,
                   unit,
                   quality,
                   freshness,
                   source_timestamp,
                   receive_timestamp,
                   processed_timestamp
            FROM {CoreRuntimeMigrations.Schema}.{tableName}
            WHERE scope_id = @scope_id
            {predicate}
            ORDER BY current_position
            LIMIT @entry_limit;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        if (cursor is not null)
        {
            command.Parameters.AddWithValue(
                "cursor",
                checked((long)cursor.Value.Value));
        }
        command.Parameters.AddWithValue(
            "entry_limit",
            checked((long)entryLimit + 1));

        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new PublishedCurrentEntry(
                scopeId,
                SourceId.From(reader.GetGuid(0)),
                PointId.From(reader.GetGuid(1)),
                SourceBindingGeneration.From(
                    checked((ulong)reader.GetInt64(2))),
                SourceSessionGeneration.From(
                    checked((ulong)reader.GetInt64(3))),
                new OwnerPosition<SourceObservation>(
                    checked((ulong)reader.GetInt64(4))),
                new OwnerPosition<PublishedCurrentEntry>(
                    checked((ulong)reader.GetInt64(5))),
                TypedValue.From(reader.GetInt64(6)),
                Unit.FromSymbol(reader.GetString(7)),
                (DataQuality)reader.GetInt16(8),
                (Freshness)reader.GetInt16(9),
                SourceTimestamp.FromUtc(
                    reader.GetFieldValue<DateTimeOffset>(10)),
                ReceiveTimestamp.FromUtc(
                    reader.GetFieldValue<DateTimeOffset>(11)),
                ProcessedTimestamp.FromUtc(
                    reader.GetFieldValue<DateTimeOffset>(12))));
        }

        return entries;
    }

    private async Task SetRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SET LOCAL ROLE \"{databaseRole}\";",
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex(
        "^[a-z][a-z0-9_]{0,62}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
