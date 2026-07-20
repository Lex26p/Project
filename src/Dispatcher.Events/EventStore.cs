using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Semantics;
using Npgsql;
using NpgsqlTypes;

namespace Dispatcher.Events;

public sealed partial class EventStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;
    private readonly EventDispatcherLimits limits;

    public EventStore(
        NpgsqlDataSource dataSource,
        string databaseRole,
        IWallClock clock,
        EventDispatcherLimits limits)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(limits);
        if (!RolePattern().IsMatch(databaseRole))
        {
            throw new ArgumentException("Database role is not a valid PostgreSQL identifier.", nameof(databaseRole));
        }

        this.dataSource = dataSource;
        this.databaseRole = databaseRole;
        this.clock = clock;
        this.limits = limits;
    }

    public async Task<Result<EventAcceptanceResult>> AcceptAlarmOccurrenceAsync(
        AlarmOccurrenceSnapshot occurrence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var acceptedAt = clock.GetUtcNow();
        if (acceptedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Event acceptance requires a UTC wall clock.");
        }

        var snapshotDto = OccurrenceDto.From(occurrence);
        var snapshotJson = JsonSerializer.Serialize(snapshotDto);
        var projectionFingerprint = Hash(snapshotJson);
        var sourceFingerprint = Hash(JsonSerializer.Serialize(new
        {
            OccurrenceId = occurrence.OccurrenceId.Value,
            ScopeId = occurrence.ScopeId.Value,
            DefinitionEpoch = occurrence.DefinitionEpoch.Value,
            DefinitionId = occurrence.DefinitionId.Value,
            PointId = occurrence.PointId.Value,
            occurrence.OpenedAt,
            occurrence.ClosedAt,
            State = (int)occurrence.Condition.State,
            occurrence.Condition.PendingSince,
            occurrence.Condition.ActiveSince,
            occurrence.Condition.ClearedAt,
            Version = occurrence.Condition.Version.Value,
        }));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, occurrence.ScopeId, cancellationToken).ConfigureAwait(false);
        await EnsureScopeAsync(connection, transaction, occurrence.ScopeId, cancellationToken).ConfigureAwait(false);

        var existingEvent = await ReadSourceEventAsync(
            connection,
            transaction,
            occurrence.ScopeId,
            occurrence.OccurrenceId,
            occurrence.Condition.Version,
            cancellationToken).ConfigureAwait(false);
        OperationalEventRecord eventRecord;
        var eventInserted = false;
        if (existingEvent is not null)
        {
            if (!string.Equals(existingEvent.Value.Fingerprint, sourceFingerprint, StringComparison.Ordinal))
            {
                return Failure<EventAcceptanceResult>(
                    "event.source_conflict",
                    "The Alarm condition version was already accepted with different content.");
            }

            eventRecord = existingEvent.Value.Event;
        }
        else
        {
            var eventPosition = await AllocateEventPositionAsync(
                connection,
                transaction,
                occurrence.ScopeId,
                cancellationToken).ConfigureAwait(false);
            eventRecord = new OperationalEventRecord(
                OperationalEventId.New(),
                eventPosition,
                occurrence.ScopeId,
                occurrence.PointId,
                occurrence.OccurrenceId,
                occurrence.Condition.Version,
                Kind(occurrence.Condition.State),
                OccurredAt(occurrence),
                acceptedAt);
            await InsertEventAsync(
                connection,
                transaction,
                eventRecord,
                sourceFingerprint,
                cancellationToken).ConfigureAwait(false);
            eventInserted = true;
        }

        var existingProjection = await ReadProjectionHeadAsync(
            connection,
            transaction,
            occurrence.ScopeId,
            occurrence.OccurrenceId,
            cancellationToken).ConfigureAwait(false);
        OccurrenceProjectionRecord projection;
        var projectionChanged = false;
        if (existingProjection is not null &&
            string.Equals(existingProjection.Fingerprint, projectionFingerprint, StringComparison.Ordinal))
        {
            projection = new OccurrenceProjectionRecord(existingProjection.Version, occurrence);
        }
        else
        {
            if (existingProjection is not null && !Advances(existingProjection.Versions, occurrence))
            {
                return Failure<EventAcceptanceResult>(
                    "event.projection_version",
                    "Occurrence facet versions must advance independently without regression.");
            }

            var projectionVersion = await AllocateProjectionVersionAsync(
                connection,
                transaction,
                occurrence.ScopeId,
                cancellationToken).ConfigureAwait(false);
            projection = new OccurrenceProjectionRecord(projectionVersion, occurrence);
            await WriteProjectionAsync(
                connection,
                transaction,
                projection,
                projectionFingerprint,
                snapshotJson,
                acceptedAt,
                cancellationToken).ConfigureAwait(false);
            projectionChanged = true;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new EventAcceptanceResult(
            eventRecord,
            projection,
            eventInserted,
            projectionChanged));
    }

    public async Task<Result<EventQueryPage>> QueryAsync(
        EventQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PageSize <= 0 || request.PageSize > limits.MaxPageSize)
        {
            return Failure<EventQueryPage>("event.page_size", "Event query page size is outside configured limits.");
        }

        if (request.From is not null && request.From.Value.Offset != TimeSpan.Zero ||
            request.To is not null && request.To.Value.Offset != TimeSpan.Zero ||
            request.From is not null && request.To is not null && request.To <= request.From)
        {
            return Failure<EventQueryPage>("event.range", "Event query requires a valid UTC time range.");
        }

        if (request.PointIds.Count == 0)
        {
            return Result.Success(new EventQueryPage([], null, null));
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var currentUpper = await ReadCurrentEventPositionAsync(
            connection,
            transaction,
            request.ScopeId,
            cancellationToken).ConfigureAwait(false);
        if (currentUpper == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(new EventQueryPage([], null, null));
        }

        var upper = request.Cursor?.UpperBound.Value ?? currentUpper;
        var after = request.Cursor?.After.Value ?? 0;
        if (upper > currentUpper || after > upper)
        {
            return Failure<EventQueryPage>("event.cursor", "Event query cursor is invalid for this scope.");
        }

        var events = await ReadEventsAsync(
            connection,
            transaction,
            request,
            after,
            upper,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = events.Count > request.PageSize;
        var page = events.Take(request.PageSize).ToArray();
        var next = hasMore
            ? new EventQueryCursor(page[^1].Position, new EventJournalPosition(upper))
            : null;
        return Result.Success(new EventQueryPage(page, new EventJournalPosition(upper), next));
    }

    public async Task<EventDispatcherCounters> CountAsync(
        EventQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PointIds.Count == 0)
        {
            return new EventDispatcherCounters(0, 0, 0);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT
                (SELECT count(*) FROM {EventMigrations.Schema}.journal_event
                 WHERE scope_id = @scope_id
                   AND point_id = ANY(@point_ids)
                   AND (@from_at IS NULL OR occurred_at >= @from_at)
                   AND (@to_at IS NULL OR occurred_at < @to_at)
                   AND (cardinality(@kinds) = 0 OR kind = ANY(@kinds))),
                (SELECT count(*) FROM {EventMigrations.Schema}.occurrence_projection
                 WHERE scope_id = @scope_id AND point_id = ANY(@point_ids)
                   AND condition_state IN (3, 4)),
                (SELECT count(*) FROM {EventMigrations.Schema}.occurrence_projection
                 WHERE scope_id = @scope_id AND point_id = ANY(@point_ids)
                   AND condition_state IN (3, 4) AND acknowledgement_state = 1);
            """,
            connection,
            transaction);
        AddFilterParameters(command, request);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var counters = new EventDispatcherCounters(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return counters;
    }

    public async Task<IReadOnlySet<PointId>> ReadKnownPointIdsAsync(
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT point_id FROM {EventMigrations.Schema}.occurrence_projection WHERE scope_id = @scope_id
            UNION
            SELECT point_id FROM {EventMigrations.Schema}.journal_event WHERE scope_id = @scope_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var points = new HashSet<PointId>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            points.Add(PointId.From(reader.GetGuid(0)));
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return points;
    }

    public async Task<OccurrenceProjectionSnapshot> ReadProjectionSnapshotAsync(
        RuntimeScopeId scopeId,
        IReadOnlySet<PointId> pointIds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var cursor = await ReadCurrentProjectionVersionAsync(
            connection,
            transaction,
            scopeId,
            cancellationToken).ConfigureAwait(false);
        var occurrences = pointIds.Count == 0
            ? []
            : await ReadProjectionSnapshotAsync(
                connection,
                transaction,
                scopeId,
                pointIds,
                cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new OccurrenceProjectionSnapshot(cursor, occurrences);
    }

    public async Task<OccurrenceFeedPage> ReadProjectionFeedAsync(
        RuntimeScopeId scopeId,
        ulong cursor,
        IReadOnlySet<PointId> pointIds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var current = await ReadCurrentProjectionVersionAsync(
            connection,
            transaction,
            scopeId,
            cancellationToken).ConfigureAwait(false);
        var earliest = current > (ulong)limits.RetainedProjectionChanges
            ? current - (ulong)limits.RetainedProjectionChanges
            : 0;
        if (cursor > current || cursor < earliest)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new OccurrenceFeedPage(OccurrenceFeedKind.Gap, cursor, current, []);
        }

        var raw = await ReadProjectionChangesAsync(
            connection,
            transaction,
            scopeId,
            cursor,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (raw.Count == 0)
        {
            return new OccurrenceFeedPage(OccurrenceFeedKind.NoChange, cursor, current, []);
        }

        var to = raw[^1].Version.Value;
        var allowed = raw.Where(item => pointIds.Contains(item.Occurrence.PointId)).ToArray();
        return new OccurrenceFeedPage(
            allowed.Length == 0 ? OccurrenceFeedKind.NoChange : OccurrenceFeedKind.Delta,
            cursor,
            to,
            allowed);
    }

    private static async Task<List<OperationalEventRecord>> ReadEventsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EventQueryRequest request,
        ulong after,
        ulong upper,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT event_id, event_position, point_id, occurrence_id, source_condition_version,
                   kind, occurred_at, accepted_at
            FROM {EventMigrations.Schema}.journal_event
            WHERE scope_id = @scope_id
              AND event_position > @after AND event_position <= @upper
              AND point_id = ANY(@point_ids)
              AND (@from_at IS NULL OR occurred_at >= @from_at)
              AND (@to_at IS NULL OR occurred_at < @to_at)
              AND (cardinality(@kinds) = 0 OR kind = ANY(@kinds))
            ORDER BY event_position
            LIMIT @limit;
            """,
            connection,
            transaction);
        AddFilterParameters(command, request);
        command.Parameters.AddWithValue("after", checked((long)after));
        command.Parameters.AddWithValue("upper", checked((long)upper));
        command.Parameters.AddWithValue("limit", request.PageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var events = new List<OperationalEventRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new OperationalEventRecord(
                OperationalEventId.From(reader.GetGuid(0)),
                new EventJournalPosition(checked((ulong)reader.GetInt64(1))),
                request.ScopeId,
                PointId.From(reader.GetGuid(2)),
                AlarmOccurrenceId.From(reader.GetGuid(3)),
                StateVersion.From(checked((ulong)reader.GetInt64(4))),
                (OperationalEventKind)reader.GetInt16(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return events;
    }

    private static async Task<IReadOnlyList<OccurrenceProjectionRecord>> ReadProjectionSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        IReadOnlySet<PointId> pointIds,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT projection_version, snapshot::text
            FROM {EventMigrations.Schema}.occurrence_projection
            WHERE scope_id = @scope_id AND point_id = ANY(@point_ids)
            ORDER BY projection_version;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("point_ids", pointIds.Select(item => item.Value).ToArray());
        return await ReadProjectionRecordsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<OccurrenceProjectionRecord>> ReadProjectionChangesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        ulong cursor,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT projection_version, snapshot::text
            FROM {EventMigrations.Schema}.projection_change
            WHERE scope_id = @scope_id AND projection_version > @cursor
            ORDER BY projection_version
            LIMIT @limit;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("cursor", checked((long)cursor));
        command.Parameters.AddWithValue("limit", limits.MaxFeedChanges);
        return await ReadProjectionRecordsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<OccurrenceProjectionRecord>> ReadProjectionRecordsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<OccurrenceProjectionRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var dto = JsonSerializer.Deserialize<OccurrenceDto>(reader.GetString(1))
                ?? throw new InvalidOperationException("Stored occurrence projection is invalid.");
            records.Add(new OccurrenceProjectionRecord(
                new OccurrenceProjectionVersion(checked((ulong)reader.GetInt64(0))),
                dto.ToModel()));
        }

        return records;
    }

    private static async Task<(OperationalEventRecord Event, string Fingerprint)?> ReadSourceEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        AlarmOccurrenceId occurrenceId,
        StateVersion conditionVersion,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT event_id, event_position, point_id, kind, occurred_at, accepted_at, source_fingerprint
            FROM {EventMigrations.Schema}.journal_event
            WHERE scope_id = @scope_id AND occurrence_id = @occurrence_id
              AND source_condition_version = @source_condition_version;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("occurrence_id", occurrenceId.Value);
        command.Parameters.AddWithValue("source_condition_version", checked((long)conditionVersion.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (
            new OperationalEventRecord(
                OperationalEventId.From(reader.GetGuid(0)),
                new EventJournalPosition(checked((ulong)reader.GetInt64(1))),
                scopeId,
                PointId.From(reader.GetGuid(2)),
                occurrenceId,
                conditionVersion,
                (OperationalEventKind)reader.GetInt16(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetFieldValue<DateTimeOffset>(5)),
            reader.GetString(6));
    }

    private static async Task<ProjectionHead?> ReadProjectionHeadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        AlarmOccurrenceId occurrenceId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT projection_version, condition_version, acknowledgement_version,
                   assignment_version, shelving_version, suppression_version, fingerprint
            FROM {EventMigrations.Schema}.occurrence_projection
            WHERE scope_id = @scope_id AND occurrence_id = @occurrence_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("occurrence_id", occurrenceId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ProjectionHead(
            new OccurrenceProjectionVersion(checked((ulong)reader.GetInt64(0))),
            new FacetVersions(
                checked((ulong)reader.GetInt64(1)),
                checked((ulong)reader.GetInt64(2)),
                checked((ulong)reader.GetInt64(3)),
                checked((ulong)reader.GetInt64(4)),
                checked((ulong)reader.GetInt64(5))),
            reader.GetString(6));
    }

    private static async Task WriteProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OccurrenceProjectionRecord projection,
        string fingerprint,
        string snapshotJson,
        DateTimeOffset projectedAt,
        CancellationToken cancellationToken)
    {
        var occurrence = projection.Occurrence;
        await using (var command = new NpgsqlCommand(
                         $"""
                         INSERT INTO {EventMigrations.Schema}.occurrence_projection (
                             scope_id, occurrence_id, point_id, projection_version,
                             condition_state, acknowledgement_state, condition_version,
                             acknowledgement_version, assignment_version, shelving_version,
                             suppression_version, fingerprint, snapshot, projected_at)
                         VALUES (
                             @scope_id, @occurrence_id, @point_id, @projection_version,
                             @condition_state, @acknowledgement_state, @condition_version,
                             @acknowledgement_version, @assignment_version, @shelving_version,
                             @suppression_version, @fingerprint, CAST(@snapshot AS jsonb), @projected_at)
                         ON CONFLICT (scope_id, occurrence_id) DO UPDATE
                         SET point_id = EXCLUDED.point_id,
                             projection_version = EXCLUDED.projection_version,
                             condition_state = EXCLUDED.condition_state,
                             acknowledgement_state = EXCLUDED.acknowledgement_state,
                             condition_version = EXCLUDED.condition_version,
                             acknowledgement_version = EXCLUDED.acknowledgement_version,
                             assignment_version = EXCLUDED.assignment_version,
                             shelving_version = EXCLUDED.shelving_version,
                             suppression_version = EXCLUDED.suppression_version,
                             fingerprint = EXCLUDED.fingerprint,
                             snapshot = EXCLUDED.snapshot,
                             projected_at = EXCLUDED.projected_at;
                         """,
                         connection,
                         transaction))
        {
            AddProjectionParameters(command, projection, fingerprint, snapshotJson, projectedAt);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var change = new NpgsqlCommand(
            $"""
            INSERT INTO {EventMigrations.Schema}.projection_change (
                scope_id, projection_version, occurrence_id, point_id, snapshot, projected_at)
            VALUES (
                @scope_id, @projection_version, @occurrence_id, @point_id,
                CAST(@snapshot AS jsonb), @projected_at);
            """,
            connection,
            transaction);
        AddProjectionParameters(change, projection, fingerprint, snapshotJson, projectedAt);
        await change.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddProjectionParameters(
        NpgsqlCommand command,
        OccurrenceProjectionRecord projection,
        string fingerprint,
        string snapshotJson,
        DateTimeOffset projectedAt)
    {
        var occurrence = projection.Occurrence;
        command.Parameters.AddWithValue("scope_id", occurrence.ScopeId.Value);
        command.Parameters.AddWithValue("occurrence_id", occurrence.OccurrenceId.Value);
        command.Parameters.AddWithValue("point_id", occurrence.PointId.Value);
        command.Parameters.AddWithValue("projection_version", checked((long)projection.Version.Value));
        command.Parameters.AddWithValue("condition_state", (short)occurrence.Condition.State);
        command.Parameters.AddWithValue("acknowledgement_state", (short)occurrence.Acknowledgement.State);
        command.Parameters.AddWithValue("condition_version", checked((long)occurrence.Condition.Version.Value));
        command.Parameters.AddWithValue(
            "acknowledgement_version",
            checked((long)occurrence.Acknowledgement.Version.Value));
        command.Parameters.AddWithValue("assignment_version", checked((long)occurrence.Assignment.Version.Value));
        command.Parameters.AddWithValue("shelving_version", checked((long)occurrence.Shelving.Version.Value));
        command.Parameters.AddWithValue("suppression_version", checked((long)occurrence.Suppression.Version.Value));
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        command.Parameters.AddWithValue("snapshot", snapshotJson);
        command.Parameters.AddWithValue("projected_at", projectedAt);
    }

    private static async Task InsertEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperationalEventRecord record,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {EventMigrations.Schema}.journal_event (
                scope_id, event_position, event_id, point_id, occurrence_id,
                source_condition_version, source_fingerprint, kind, occurred_at, accepted_at)
            VALUES (
                @scope_id, @event_position, @event_id, @point_id, @occurrence_id,
                @source_condition_version, @source_fingerprint, @kind, @occurred_at, @accepted_at);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", record.ScopeId.Value);
        command.Parameters.AddWithValue("event_position", checked((long)record.Position.Value));
        command.Parameters.AddWithValue("event_id", record.EventId.Value);
        command.Parameters.AddWithValue("point_id", record.PointId.Value);
        command.Parameters.AddWithValue("occurrence_id", record.OccurrenceId.Value);
        command.Parameters.AddWithValue("source_condition_version", checked((long)record.SourceConditionVersion.Value));
        command.Parameters.AddWithValue("source_fingerprint", fingerprint);
        command.Parameters.AddWithValue("kind", (short)record.Kind);
        command.Parameters.AddWithValue("occurred_at", record.OccurredAt);
        command.Parameters.AddWithValue("accepted_at", record.AcceptedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<EventJournalPosition> AllocateEventPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        var value = await AllocateAsync(
            connection,
            transaction,
            scopeId,
            "next_event_position",
            cancellationToken).ConfigureAwait(false);
        return new EventJournalPosition(value);
    }

    private static async Task<OccurrenceProjectionVersion> AllocateProjectionVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        var value = await AllocateAsync(
            connection,
            transaction,
            scopeId,
            "next_projection_version",
            cancellationToken).ConfigureAwait(false);
        return new OccurrenceProjectionVersion(value);
    }

    private static async Task<ulong> AllocateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {EventMigrations.Schema}.scope_state
            SET {column} = {column} + 1
            WHERE scope_id = @scope_id
            RETURNING {column} - 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        return checked((ulong)(long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
    }

    private static async Task<ulong> ReadCurrentEventPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken) =>
        await ReadCurrentAsync(
            connection,
            transaction,
            scopeId,
            "next_event_position",
            cancellationToken).ConfigureAwait(false);

    private static async Task<ulong> ReadCurrentProjectionVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken) =>
        await ReadCurrentAsync(
            connection,
            transaction,
            scopeId,
            "next_projection_version",
            cancellationToken).ConfigureAwait(false);

    private static async Task<ulong> ReadCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT {column} - 1 FROM {EventMigrations.Schema}.scope_state WHERE scope_id = @scope_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? 0 : checked((ulong)(long)value);
    }

    private static async Task EnsureScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {EventMigrations.Schema}.scope_state (scope_id) VALUES (@scope_id) ON CONFLICT DO NOTHING;",
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

    private static void AddFilterParameters(NpgsqlCommand command, EventQueryRequest request)
    {
        command.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
        command.Parameters.AddWithValue(
            "point_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            request.PointIds.Select(item => item.Value).ToArray());
        command.Parameters.AddWithValue(
            "kinds",
            NpgsqlDbType.Array | NpgsqlDbType.Smallint,
            request.Kinds.Select(item => (short)item).ToArray());
        command.Parameters.AddWithValue(
            "from_at",
            NpgsqlDbType.TimestampTz,
            (object?)request.From ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "to_at",
            NpgsqlDbType.TimestampTz,
            (object?)request.To ?? DBNull.Value);
    }

    private static bool Advances(FacetVersions existing, AlarmOccurrenceSnapshot occurrence)
    {
        var current = new FacetVersions(
            occurrence.Condition.Version.Value,
            occurrence.Acknowledgement.Version.Value,
            occurrence.Assignment.Version.Value,
            occurrence.Shelving.Version.Value,
            occurrence.Suppression.Version.Value);
        return current.Condition >= existing.Condition &&
               current.Acknowledgement >= existing.Acknowledgement &&
               current.Assignment >= existing.Assignment &&
               current.Shelving >= existing.Shelving &&
               current.Suppression >= existing.Suppression &&
               current != existing;
    }

    private static OperationalEventKind Kind(AlarmConditionState state) => state switch
    {
        AlarmConditionState.Active => OperationalEventKind.AlarmRaised,
        AlarmConditionState.PendingClear => OperationalEventKind.AlarmPendingClear,
        AlarmConditionState.Normal => OperationalEventKind.AlarmCleared,
        _ => throw new ArgumentOutOfRangeException(nameof(state), "Pending raise has no Alarm occurrence."),
    };

    private static DateTimeOffset OccurredAt(AlarmOccurrenceSnapshot occurrence) => occurrence.Condition.State switch
    {
        AlarmConditionState.Active => occurrence.Condition.ActiveSince ?? occurrence.OpenedAt,
        AlarmConditionState.PendingClear => occurrence.Condition.PendingSince ?? occurrence.OpenedAt,
        AlarmConditionState.Normal => occurrence.Condition.ClearedAt ?? occurrence.ClosedAt ?? occurrence.OpenedAt,
        _ => throw new ArgumentOutOfRangeException(nameof(occurrence), "Pending raise has no Alarm occurrence."),
    };

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));

    private sealed record FacetVersions(
        ulong Condition,
        ulong Acknowledgement,
        ulong Assignment,
        ulong Shelving,
        ulong Suppression);

    private sealed record ProjectionHead(
        OccurrenceProjectionVersion Version,
        FacetVersions Versions,
        string Fingerprint);

    private sealed record OccurrenceDto(
        Guid OccurrenceId,
        Guid ScopeId,
        ulong DefinitionEpoch,
        Guid DefinitionId,
        Guid PointId,
        DateTimeOffset OpenedAt,
        DateTimeOffset? ClosedAt,
        int ConditionState,
        DateTimeOffset? ConditionPendingSince,
        DateTimeOffset? ConditionActiveSince,
        DateTimeOffset? ConditionClearedAt,
        ulong ConditionVersion,
        int AcknowledgementState,
        Guid? AcknowledgedBy,
        DateTimeOffset? AcknowledgedAt,
        ulong AcknowledgementVersion,
        Guid? AssignedTo,
        DateTimeOffset? AssignedAt,
        ulong AssignmentVersion,
        DateTimeOffset? ShelvedUntil,
        string? ShelvingReason,
        ulong ShelvingVersion,
        bool IsSuppressed,
        string? SuppressionReason,
        ulong SuppressionVersion)
    {
        public static OccurrenceDto From(AlarmOccurrenceSnapshot occurrence) => new(
            occurrence.OccurrenceId.Value,
            occurrence.ScopeId.Value,
            occurrence.DefinitionEpoch.Value,
            occurrence.DefinitionId.Value,
            occurrence.PointId.Value,
            occurrence.OpenedAt,
            occurrence.ClosedAt,
            (int)occurrence.Condition.State,
            occurrence.Condition.PendingSince,
            occurrence.Condition.ActiveSince,
            occurrence.Condition.ClearedAt,
            occurrence.Condition.Version.Value,
            (int)occurrence.Acknowledgement.State,
            occurrence.Acknowledgement.AcknowledgedBy,
            occurrence.Acknowledgement.AcknowledgedAt,
            occurrence.Acknowledgement.Version.Value,
            occurrence.Assignment.AssignedTo,
            occurrence.Assignment.AssignedAt,
            occurrence.Assignment.Version.Value,
            occurrence.Shelving.ShelvedUntil,
            occurrence.Shelving.Reason,
            occurrence.Shelving.Version.Value,
            occurrence.Suppression.IsSuppressed,
            occurrence.Suppression.Reason,
            occurrence.Suppression.Version.Value);

        public AlarmOccurrenceSnapshot ToModel() => new(
            AlarmOccurrenceId.From(OccurrenceId),
            RuntimeScopeId.From(ScopeId),
            RevisionNumber.From(DefinitionEpoch),
            AlarmDefinitionId.From(DefinitionId),
            Dispatcher.Semantics.PointId.From(PointId),
            OpenedAt,
            ClosedAt,
            new AlarmConditionFacet(
                (AlarmConditionState)ConditionState,
                ConditionPendingSince,
                ConditionActiveSince,
                ConditionClearedAt,
                StateVersion.From(ConditionVersion)),
            new AlarmAcknowledgementFacet(
                (AlarmAcknowledgementState)AcknowledgementState,
                AcknowledgedBy,
                AcknowledgedAt,
                StateVersion.From(AcknowledgementVersion)),
            new AlarmAssignmentFacet(AssignedTo, AssignedAt, StateVersion.From(AssignmentVersion)),
            new AlarmShelvingFacet(ShelvedUntil, ShelvingReason, StateVersion.From(ShelvingVersion)),
            new AlarmSuppressionFacet(IsSuppressed, SuppressionReason, StateVersion.From(SuppressionVersion)));
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
