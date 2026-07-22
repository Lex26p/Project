using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;
using NpgsqlTypes;

namespace Dispatcher.Administration;

public sealed class AdministrationStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly AdministrationQueryLimits limits;

    public AdministrationStore(NpgsqlDataSource dataSource, string databaseRole, AdministrationQueryLimits limits)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        if (databaseRole.Any(value => !char.IsAsciiLetterOrDigit(value) && value != '_'))
            throw new ArgumentException("Invalid PostgreSQL role name.", nameof(databaseRole));
        this.databaseRole = databaseRole;
        this.limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public async Task<Result> AcceptHealthAsync(OwnerHealthFact fact, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!ValidOwner(fact.Owner) || !ValidText(fact.ReasonCode, 200) ||
            !Enum.IsDefined(fact.Facet) || !Enum.IsDefined(fact.State) || fact.ObservedAt.Offset != TimeSpan.Zero)
            return Failure("administration.health_invalid", "Owner health fact is invalid.");
        var fingerprint = Fingerprint(
            fact.Owner, fact.ScopeId, fact.RequiredPermission.Value, (short)fact.Facet, (short)fact.State,
            fact.ReasonCode, fact.ObservedAt);
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {AdministrationMigrations.Schema}.health_fact
                (fact_id,fingerprint,owner,scope_id,required_permission,facet,state,reason_code,observed_at)
            VALUES (@id,@fingerprint,@owner,@scope,@permission,@facet,@state,@reason,@observed)
            ON CONFLICT (fact_id) DO NOTHING;
            """, connection, transaction);
        command.Parameters.AddWithValue("id", fact.FactId);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        command.Parameters.AddWithValue("owner", fact.Owner.Trim());
        AddNullableUuid(command, "scope", fact.ScopeId);
        command.Parameters.AddWithValue("permission", fact.RequiredPermission.Value);
        command.Parameters.AddWithValue("facet", (short)fact.Facet);
        command.Parameters.AddWithValue("state", (short)fact.State);
        command.Parameters.AddWithValue("reason", fact.ReasonCode.Trim());
        command.Parameters.AddWithValue("observed", fact.ObservedAt);
        var inserted = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var accepted = inserted == 1 || await SameFingerprintAsync(
            connection, transaction, "health_fact", "fact_id", fact.FactId, fingerprint, token).ConfigureAwait(false);
        if (!accepted) return Failure("administration.fact_conflict", "Fact identity was reused with different content.");
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result> AcceptDataQualityAsync(OwnerDataQualityFact fact, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!ValidOwner(fact.Owner) || !ValidText(fact.Summary, 500) || !ValidText(fact.SourceReference, 300) ||
            !Enum.IsDefined(fact.Kind) || !Enum.IsDefined(fact.Severity) || !Enum.IsDefined(fact.State) ||
            fact.ObservedAt.Offset != TimeSpan.Zero)
            return Failure("administration.data_quality_invalid", "Owner data-quality fact is invalid.");
        var fingerprint = Fingerprint(fact.IssueId, fact.Owner, fact.ScopeId, fact.RequiredPermission.Value,
            (short)fact.Kind, (short)fact.Severity, (short)fact.State, fact.Summary, fact.SourceReference, fact.ObservedAt);
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {AdministrationMigrations.Schema}.data_quality_fact
                (fact_id,issue_id,fingerprint,owner,scope_id,required_permission,kind,severity,state,summary,source_reference,observed_at)
            VALUES (@id,@issue,@fingerprint,@owner,@scope,@permission,@kind,@severity,@state,@summary,@source,@observed)
            ON CONFLICT (fact_id) DO NOTHING;
            """, connection, transaction);
        command.Parameters.AddWithValue("id", fact.FactId);
        command.Parameters.AddWithValue("issue", fact.IssueId);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        command.Parameters.AddWithValue("owner", fact.Owner.Trim());
        AddNullableUuid(command, "scope", fact.ScopeId);
        command.Parameters.AddWithValue("permission", fact.RequiredPermission.Value);
        command.Parameters.AddWithValue("kind", (short)fact.Kind);
        command.Parameters.AddWithValue("severity", (short)fact.Severity);
        command.Parameters.AddWithValue("state", (short)fact.State);
        command.Parameters.AddWithValue("summary", fact.Summary.Trim());
        command.Parameters.AddWithValue("source", fact.SourceReference.Trim());
        command.Parameters.AddWithValue("observed", fact.ObservedAt);
        var inserted = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var accepted = inserted == 1 || await SameFingerprintAsync(
            connection, transaction, "data_quality_fact", "fact_id", fact.FactId, fingerprint, token).ConfigureAwait(false);
        if (!accepted) return Failure("administration.fact_conflict", "Fact identity was reused with different content.");
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result> AcceptAuditAsync(OwnerAuditFact fact, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (!ValidOwner(fact.Owner) || !ValidText(fact.Action, 200) || !ValidText(fact.TargetKind, 100) ||
            !ValidText(fact.Summary, 500) || !Enum.IsDefined(fact.Outcome) ||
            (fact.PrincipalKind is not null && !Enum.IsDefined(fact.PrincipalKind.Value)) ||
            fact.OccurredAt.Offset != TimeSpan.Zero)
            return Failure("administration.audit_invalid", "Owner audit fact is invalid.");
        var fingerprint = Fingerprint(fact.Owner, fact.ScopeId, fact.RequiredPermission.Value,
            fact.SessionId?.Value, fact.SubjectId?.Value, fact.PrincipalKind, fact.Action,
            fact.TargetKind, fact.TargetId, (short)fact.Outcome, fact.Summary, fact.OccurredAt);
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {AdministrationMigrations.Schema}.audit_record
                (event_id,fingerprint,owner,scope_id,required_permission,session_id,subject_id,principal_kind,
                 action,target_kind,target_id,outcome,summary,occurred_at)
            VALUES (@id,@fingerprint,@owner,@scope,@permission,@session,@subject,@principal,
                    @action,@target_kind,@target_id,@outcome,@summary,@occurred)
            ON CONFLICT (event_id) DO NOTHING;
            """, connection, transaction);
        command.Parameters.AddWithValue("id", fact.EventId);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        command.Parameters.AddWithValue("owner", fact.Owner.Trim());
        AddNullableUuid(command, "scope", fact.ScopeId);
        command.Parameters.AddWithValue("permission", fact.RequiredPermission.Value);
        AddNullableUuid(command, "session", fact.SessionId?.Value);
        AddNullableUuid(command, "subject", fact.SubjectId?.Value);
        command.Parameters.Add(new NpgsqlParameter("principal", NpgsqlDbType.Smallint)
            { Value = fact.PrincipalKind is null ? DBNull.Value : (short)fact.PrincipalKind.Value });
        command.Parameters.AddWithValue("action", fact.Action.Trim());
        command.Parameters.AddWithValue("target_kind", fact.TargetKind.Trim());
        AddNullableUuid(command, "target_id", fact.TargetId);
        command.Parameters.AddWithValue("outcome", (short)fact.Outcome);
        command.Parameters.AddWithValue("summary", fact.Summary.Trim());
        command.Parameters.AddWithValue("occurred", fact.OccurredAt);
        var inserted = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var accepted = inserted == 1 || await SameFingerprintAsync(
            connection, transaction, "audit_record", "event_id", fact.EventId, fingerprint, token).ConfigureAwait(false);
        if (!accepted) return Failure("administration.audit_conflict", "Audit identity was reused with different content.");
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<OperationalHealthView>> ReadHealthAsync(
        AuthorizedAccess authorization, CancellationToken token = default)
    {
        if (authorization.Permission != AdministrationPermissions.ReadHealth)
            return Failure<OperationalHealthView>("permission.denied", "Health read permission is required.");
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            WITH current AS (
                SELECT DISTINCT ON (owner,scope_id,facet)
                    owner,scope_id,facet,state,reason_code,observed_at,required_permission
                FROM {AdministrationMigrations.Schema}.health_fact
                ORDER BY owner,scope_id,facet,position DESC)
            SELECT owner,scope_id,facet,state,reason_code,observed_at FROM current
            WHERE required_permission=ANY(@grants) AND NOT (required_permission=ANY(@denials))
            ORDER BY owner,scope_id,facet LIMIT @limit;
            """, connection, transaction);
        AddPermissionParameters(command, authorization.Session);
        command.Parameters.AddWithValue("limit", limits.MaximumViewItems + 1);
        var items = new List<OperationalHealthItem>();
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) items.Add(new(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1),
                (OperationalHealthFacet)reader.GetInt16(2), (OperationalHealthState)reader.GetInt16(3),
                reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
        if (items.Count > limits.MaximumViewItems)
            return Failure<OperationalHealthView>("administration.view_overload", "Visible health view exceeds its configured bound.");
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(new OperationalHealthView(items,
            items.Count(value => value.State == OperationalHealthState.Healthy),
            items.Count(value => value.State == OperationalHealthState.Degraded),
            items.Count(value => value.State == OperationalHealthState.Unavailable),
            items.Count(value => value.State == OperationalHealthState.Unknown)));
    }

    public async Task<Result<DataQualityView>> ReadDataQualityAsync(
        AuthorizedAccess authorization, CancellationToken token = default)
    {
        if (authorization.Permission != AdministrationPermissions.ReadDataQuality)
            return Failure<DataQualityView>("permission.denied", "Data-quality read permission is required.");
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            WITH current AS (
                SELECT DISTINCT ON (issue_id)
                    issue_id,owner,scope_id,kind,severity,state,summary,source_reference,observed_at,required_permission
                FROM {AdministrationMigrations.Schema}.data_quality_fact
                ORDER BY issue_id,position DESC)
            SELECT issue_id,owner,scope_id,kind,severity,summary,source_reference,observed_at FROM current
            WHERE state=1 AND required_permission=ANY(@grants) AND NOT (required_permission=ANY(@denials))
            ORDER BY severity DESC,observed_at DESC,issue_id LIMIT @limit;
            """, connection, transaction);
        AddPermissionParameters(command, authorization.Session);
        command.Parameters.AddWithValue("limit", limits.MaximumViewItems + 1);
        var items = new List<DataQualityIssueItem>();
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) items.Add(new(
                reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                (DataQualityIssueKind)reader.GetInt16(3), (OperationalSeverity)reader.GetInt16(4),
                reader.GetString(5), reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7)));
        if (items.Count > limits.MaximumViewItems)
            return Failure<DataQualityView>("administration.view_overload", "Visible data-quality view exceeds its configured bound.");
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(new DataQualityView(items,
            items.Count(value => value.Severity == OperationalSeverity.Warning),
            items.Count(value => value.Severity == OperationalSeverity.Critical)));
    }

    public async Task<Result<AuditFeedPage>> ReadAuditAsync(
        AuthorizedAccess authorization, AuditQuery query, CancellationToken token = default)
    {
        if (authorization.Permission != AdministrationPermissions.ReadAudit)
            return Failure<AuditFeedPage>("permission.denied", "Audit read permission is required.");
        ArgumentNullException.ThrowIfNull(query);
        if (query.PageSize <= 0 || query.PageSize > limits.MaximumAuditPageSize)
            return Failure<AuditFeedPage>("administration.audit_page_invalid", "Audit page size exceeds its configured bound.");
        var hasCursor = query.Cursor is not null;
        ulong after = 0;
        if (hasCursor && !TryDecodeCursor(query.Cursor!, out after))
            return Failure<AuditFeedPage>("administration.audit_cursor_invalid", "Audit cursor is invalid.");
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        var permissions = authorization.Session;
        var latest = await ReadVisiblePositionAsync(connection, transaction, permissions, 0, token).ConfigureAwait(false);
        if (hasCursor && latest > 0)
        {
            var cutoff = await ReadVisiblePositionAsync(
                connection, transaction, permissions, limits.RetainedAuditTail - 1, token).ConfigureAwait(false);
            if (cutoff > 0 && after < cutoff)
                return Result.Success(new AuditFeedPage(AuditFeedKind.Gap, [], null));
        }
        var descending = !hasCursor;
        await using var command = new NpgsqlCommand($"""
            SELECT position,event_id,owner,scope_id,subject_id,principal_kind,action,target_kind,target_id,outcome,summary,occurred_at
            FROM {AdministrationMigrations.Schema}.audit_record
            WHERE required_permission=ANY(@grants) AND NOT (required_permission=ANY(@denials))
              AND (@initial OR position>@after)
            ORDER BY position {(descending ? "DESC" : "ASC")} LIMIT @limit;
            """, connection, transaction);
        AddPermissionParameters(command, permissions);
        command.Parameters.AddWithValue("initial", !hasCursor);
        command.Parameters.AddWithValue("after", checked((long)after));
        command.Parameters.AddWithValue("limit", query.PageSize);
        var rows = new List<(ulong Position, AuditRecordItem Item)>();
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add((checked((ulong)reader.GetInt64(0)), new(
                reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.IsDBNull(5) ? null : (PrincipalKind)reader.GetInt16(5),
                reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetGuid(8),
                (AuditOutcome)reader.GetInt16(9), reader.GetString(10), reader.GetFieldValue<DateTimeOffset>(11))));
        if (descending) rows.Reverse();
        await transaction.CommitAsync(token).ConfigureAwait(false);
        var cursor = rows.Count == 0 ? query.Cursor : EncodeCursor(rows[^1].Position);
        return Result.Success(new AuditFeedPage(AuditFeedKind.Records, rows.Select(value => value.Item).ToArray(), cursor));
    }

    private static async Task<ulong> ReadVisiblePositionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, SessionSnapshot session, int offset,
        CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT position FROM {AdministrationMigrations.Schema}.audit_record
            WHERE required_permission=ANY(@grants) AND NOT (required_permission=ANY(@denials))
            ORDER BY position DESC OFFSET @offset LIMIT 1;
            """, connection, transaction);
        AddPermissionParameters(command, session);
        command.Parameters.AddWithValue("offset", offset);
        var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        return value is long position ? checked((ulong)position) : 0;
    }

    private static async Task<bool> SameFingerprintAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string table, string idColumn,
        Guid id, string fingerprint, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT fingerprint FROM {AdministrationMigrations.Schema}.{table} WHERE {idColumn}=@id;",
            connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return string.Equals((string?)await command.ExecuteScalarAsync(token).ConfigureAwait(false), fingerprint, StringComparison.Ordinal);
    }

    private async Task SetRoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE {databaseRole};", connection, transaction);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static void AddPermissionParameters(NpgsqlCommand command, SessionSnapshot session)
    {
        command.Parameters.AddWithValue("grants", session.Permissions.Grants.Select(value => value.Value).ToArray());
        command.Parameters.AddWithValue("denials", session.Permissions.Denials.Select(value => value.Value).ToArray());
    }

    private static void AddNullableUuid(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = value ?? (object)DBNull.Value });
    private static bool ValidOwner(string value) => ValidText(value, 100);
    private static bool ValidText(string value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    private static string Fingerprint(params object?[] values)
    {
        var canonical = string.Join('|', values.Select(value => value switch
        {
            null => "-",
            DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
            Guid id => id.ToString("N"),
            Enum enumeration => Convert.ToInt32(enumeration, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
    private static string EncodeCursor(ulong position)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, position);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    private static bool TryDecodeCursor(string cursor, out ulong position)
    {
        position = 0;
        try
        {
            var value = cursor.Replace('-', '+').Replace('_', '/');
            value = value.PadRight((value.Length + 3) / 4 * 4, '=');
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length != 8) return false;
            position = BinaryPrimitives.ReadUInt64BigEndian(bytes);
            return true;
        }
        catch (FormatException) { return false; }
    }
    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));
    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));
}
