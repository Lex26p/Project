using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dispatcher.Core;
using Dispatcher.Identity;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Simulator;
using Npgsql;
using NpgsqlTypes;

namespace Dispatcher.Command;

public sealed class CommandStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;
    private readonly CommandSecurityPolicy policy;

    public CommandStore(
        NpgsqlDataSource dataSource, string databaseRole, IWallClock clock, CommandSecurityPolicy policy)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        if (databaseRole.Any(value => !char.IsAsciiLetterOrDigit(value) && value != '_'))
            throw new ArgumentException("Invalid PostgreSQL role name.", nameof(databaseRole));
        this.databaseRole = databaseRole;
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public async Task<Result<StateVersion>> RecordSafetyBlockAsync(
        CommandSafetyBlockFact fact, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (string.IsNullOrWhiteSpace(fact.ReasonCode) || fact.ReasonCode.Length > 200 || fact.ObservedAt.Offset != TimeSpan.Zero)
            return Failure<StateVersion>("command.safety_fact_invalid", "Command safety fact is invalid.");
        var fingerprint = Fingerprint(fact.ScopeId.Value, fact.Blocked, fact.ReasonCode, fact.ObservedAt);
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        var existing = await ReadReceiptFingerprintAsync(connection, transaction, fact.FactId, token).ConfigureAwait(false);
        if (existing is not null)
            return string.Equals(existing, fingerprint, StringComparison.Ordinal)
                ? Result.Success(await ReadSafetyVersionAsync(connection, transaction, fact.ScopeId, token).ConfigureAwait(false))
                : Failure<StateVersion>("command.safety_fact_conflict", "Safety fact identity was reused with different content.");
        await using var insertGuard = new NpgsqlCommand($"""
            INSERT INTO {CommandMigrations.Schema}.scope_guard
                (scope_id,safety_version,blocked,reason_code,updated_at)
            VALUES (@scope,1,@blocked,@reason,@observed)
            ON CONFLICT (scope_id) DO NOTHING;
            """, connection, transaction);
        insertGuard.Parameters.AddWithValue("scope", fact.ScopeId.Value);
        insertGuard.Parameters.AddWithValue("blocked", fact.Blocked);
        insertGuard.Parameters.AddWithValue("reason", fact.ReasonCode.Trim());
        insertGuard.Parameters.AddWithValue("observed", fact.ObservedAt);
        var created = await insertGuard.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
        await using (var gate = new NpgsqlCommand(
            $"SELECT safety_version FROM {CommandMigrations.Schema}.scope_guard WHERE scope_id=@scope FOR UPDATE;",
            connection, transaction))
        { gate.Parameters.AddWithValue("scope", fact.ScopeId.Value); await gate.ExecuteScalarAsync(token).ConfigureAwait(false); }
        if (!created)
        {
            await using var update = new NpgsqlCommand($"""
                UPDATE {CommandMigrations.Schema}.scope_guard
                SET safety_version=safety_version+1,blocked=@blocked,reason_code=@reason,updated_at=@observed
                WHERE scope_id=@scope;
                """, connection, transaction);
            update.Parameters.AddWithValue("scope", fact.ScopeId.Value);
            update.Parameters.AddWithValue("blocked", fact.Blocked);
            update.Parameters.AddWithValue("reason", fact.ReasonCode.Trim());
            update.Parameters.AddWithValue("observed", fact.ObservedAt);
            await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await using (var receipt = new NpgsqlCommand($"""
            INSERT INTO {CommandMigrations.Schema}.safety_fact_receipt
                (fact_id,fingerprint,scope_id,accepted_at) VALUES (@id,@fingerprint,@scope,@accepted);
            """, connection, transaction))
        {
            receipt.Parameters.AddWithValue("id", fact.FactId);
            receipt.Parameters.AddWithValue("fingerprint", fingerprint);
            receipt.Parameters.AddWithValue("scope", fact.ScopeId.Value);
            receipt.Parameters.AddWithValue("accepted", UtcNow());
            await receipt.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        var version = await ReadSafetyVersionAsync(connection, transaction, fact.ScopeId, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(version);
    }

    public async Task<Result<ControlLeaseSnapshot>> AcquireLeaseAsync(
        AuthorizedMutation authorization, AcquireControlLease request, StepUpAttestation? stepUp,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        var sessionValidation = SessionAuthorization.ValidateSession(authorization.Session, clock);
        if (sessionValidation.IsFailure) return Result.Failure<ControlLeaseSnapshot>(sessionValidation.Error!);
        if (authorization.Permission != CommandPermissions.AcquireLease ||
            !authorization.Session.Permissions.Allows(CommandPermissions.ControlScope(request.ScopeId)))
            return Failure<ControlLeaseSnapshot>("permission.denied", "Lease and exact scope control permissions are required.");
        if (request.Lifetime <= TimeSpan.Zero || request.Lifetime > policy.MaximumLeaseLifetime)
            return Failure<ControlLeaseSnapshot>("command.lease_lifetime_invalid", "Control lease lifetime exceeds policy.");
        var now = UtcNow();
        if (policy.StepUpRequired && (stepUp is null || stepUp.SessionId != authorization.Session.Id ||
            stepUp.SubjectId != authorization.Session.SubjectId || stepUp.VerifiedAt > now ||
            stepUp.ExpiresAt <= now || now - stepUp.VerifiedAt > policy.StepUpLifetime))
            return Failure<ControlLeaseSnapshot>("command.step_up_required", "A current bound step-up attestation is required.");
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        _ = await ReadGuardAsync(connection, transaction, request.ScopeId, token).ConfigureAwait(false);
        await using (var expire = new NpgsqlCommand($"""
            UPDATE {CommandMigrations.Schema}.control_lease
            SET revoked_at=@now,revocation_reason='expired',version=version+1
            WHERE scope_id=@scope AND revoked_at IS NULL AND expires_at<=@now;
            """, connection, transaction))
        {
            expire.Parameters.AddWithValue("scope", request.ScopeId.Value);
            expire.Parameters.AddWithValue("now", now);
            await expire.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await using (var active = new NpgsqlCommand($"""
            SELECT 1 FROM {CommandMigrations.Schema}.control_lease
            WHERE scope_id=@scope AND revoked_at IS NULL FOR UPDATE;
            """, connection, transaction))
        {
            active.Parameters.AddWithValue("scope", request.ScopeId.Value);
            if (await active.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                return Failure<ControlLeaseSnapshot>("command.lease_held", "The scope already has an active ControlLease.");
        }
        var expiresAt = now.Add(request.Lifetime);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {CommandMigrations.Schema}.control_lease
                (lease_id,scope_id,session_id,subject_id,issued_at,expires_at,revoked_at,revocation_reason,version)
            VALUES (@lease,@scope,@session,@subject,@issued,@expires,NULL,NULL,1);
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("lease", request.LeaseId.Value);
            insert.Parameters.AddWithValue("scope", request.ScopeId.Value);
            insert.Parameters.AddWithValue("session", authorization.Session.Id.Value);
            insert.Parameters.AddWithValue("subject", authorization.Session.SubjectId.Value);
            insert.Parameters.AddWithValue("issued", now);
            insert.Parameters.AddWithValue("expires", expiresAt);
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        if (policy.StepUpRequired)
        {
            await using var consumed = new NpgsqlCommand($"""
                INSERT INTO {CommandMigrations.Schema}.used_step_up (attestation_id,lease_id,consumed_at)
                VALUES (@attestation,@lease,@now) ON CONFLICT (attestation_id) DO NOTHING;
                """, connection, transaction);
            consumed.Parameters.AddWithValue("attestation", stepUp!.AttestationId);
            consumed.Parameters.AddWithValue("lease", request.LeaseId.Value);
            consumed.Parameters.AddWithValue("now", now);
            if (await consumed.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                return Failure<ControlLeaseSnapshot>("command.step_up_replayed", "Step-up attestation was already consumed.");
        }
        await WriteAuditAsync(connection, transaction, request.LeaseId.Value, null, authorization.Session,
            "acquire-lease", "succeeded", now, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(new ControlLeaseSnapshot(
            request.LeaseId, request.ScopeId, authorization.Session.Id, authorization.Session.SubjectId,
            now, expiresAt, null, null, StateVersion.Initial));
    }

    public async Task<Result<ControlLeaseSnapshot>> RevokeLeaseAsync(
        AuthorizedMutation authorization, ControlLeaseId leaseId, string reason,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (authorization.Permission != CommandPermissions.AcquireLease || string.IsNullOrWhiteSpace(reason) || reason.Length > 200)
            return Failure<ControlLeaseSnapshot>("permission.denied", "Lease holder permission is required.");
        var validation = SessionAuthorization.ValidateSession(authorization.Session, clock);
        if (validation.IsFailure) return Result.Failure<ControlLeaseSnapshot>(validation.Error!);
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        var lease = await ReadLeaseAsync(connection, transaction, leaseId, true, token).ConfigureAwait(false);
        if (lease is null) return Failure<ControlLeaseSnapshot>("command.lease_not_found", "ControlLease was not found.");
        if (lease.SessionId != authorization.Session.Id || lease.SubjectId != authorization.Session.SubjectId)
            return Failure<ControlLeaseSnapshot>("command.lease_holder_mismatch", "Only the bound session can revoke this ControlLease.");
        if (lease.RevokedAt is null)
        {
            await using var update = new NpgsqlCommand($"""
                UPDATE {CommandMigrations.Schema}.control_lease
                SET revoked_at=@now,revocation_reason=@reason,version=version+1 WHERE lease_id=@lease;
                """, connection, transaction);
            update.Parameters.AddWithValue("lease", leaseId.Value);
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("reason", reason.Trim());
            await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            lease = lease with { RevokedAt = now, RevocationReason = reason.Trim(), Version = lease.Version.Next() };
            await WriteAuditAsync(connection, transaction, leaseId.Value, null, authorization.Session,
                "revoke-lease", "succeeded", now, token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(lease);
    }

    public async Task<Result<PreparedCommandIntent>> PrepareAsync(
        AuthorizedMutation authorization, PrepareSimulatorCommand request,
        ActiveSimulatorManifest activeManifest, CurrentSnapshot current,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(activeManifest);
        ArgumentNullException.ThrowIfNull(current);
        var validation = SessionAuthorization.ValidateSession(authorization.Session, clock);
        if (validation.IsFailure) return Result.Failure<PreparedCommandIntent>(validation.Error!);
        if (authorization.Permission != CommandPermissions.Prepare ||
            !authorization.Session.Permissions.Allows(CommandPermissions.ControlScope(request.ScopeId)) ||
            !authorization.Session.Permissions.Allows(CommandPermissions.ControlPoint(request.PointId)))
            return Failure<PreparedCommandIntent>("permission.denied", "Prepare, scope and exact point control permissions are required.");
        if (request.InteractionMode != CommandInteractionMode.Live)
            return Failure<PreparedCommandIntent>("command.history_mode_denied", "Commands cannot be prepared from History mode.");
        if (activeManifest.Configuration.ScopeId != request.ScopeId ||
            activeManifest.Receipt.RevisionId != request.ExpectedRevisionId ||
            activeManifest.Receipt.RevisionNumber != request.ExpectedRevisionNumber ||
            activeManifest.Generation != request.ExpectedGeneration ||
            !string.Equals(activeManifest.Receipt.ManifestFingerprint, request.ExpectedManifestFingerprint, StringComparison.Ordinal))
            return Failure<PreparedCommandIntent>("command.active_revision_stale", "Active Simulator revision evidence changed.");
        var point = activeManifest.Configuration.Points.SingleOrDefault(value => value.PointId == request.PointId);
        if (point is null || point.Unit != request.Unit)
            return Failure<PreparedCommandIntent>("command.target_invalid", "Target is not present in the exact active Simulator manifest.");
        if (current.ScopeId != request.ScopeId || current.Position.Value != request.ExpectedCurrentPosition)
            return Failure<PreparedCommandIntent>("command.current_stale", "Runtime current evidence changed.");
        var entry = current.Entries.SingleOrDefault(value => value.PointId == request.PointId);
        if (entry is null || entry.Unit != request.Unit)
            return Failure<PreparedCommandIntent>("command.target_unavailable", "Target current evidence is unavailable.");
        if (entry.Quality != DataQuality.Good || entry.Freshness != Freshness.Fresh)
            return Failure<PreparedCommandIntent>("command.quality_blocked", "Target quality and freshness do not permit command preparation.");
        if (request.ExpectedCurrentPosition > long.MaxValue)
            return Failure<PreparedCommandIntent>("command.current_stale", "Runtime current position is outside the durable command range.");
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        var lease = await ReadLeaseAsync(connection, transaction, request.LeaseId, true, token).ConfigureAwait(false);
        if (lease is null || lease.ScopeId != request.ScopeId)
            return Failure<PreparedCommandIntent>("command.lease_not_found", "Bound ControlLease was not found.");
        if (lease.SessionId != authorization.Session.Id || lease.SubjectId != authorization.Session.SubjectId)
            return Failure<PreparedCommandIntent>("command.lease_holder_mismatch", "ControlLease belongs to another session.");
        if (lease.RevokedAt is not null || lease.ExpiresAt <= now)
            return Failure<PreparedCommandIntent>("command.lease_inactive", "ControlLease is revoked or expired.");
        var guard = await ReadGuardAsync(connection, transaction, request.ScopeId, token).ConfigureAwait(false);
        if (guard.Blocked)
            return Failure<PreparedCommandIntent>("command.safety_blocked", "Command preparation is blocked by current safety evidence.");
        var fingerprint = Fingerprint(
            request.LeaseId.Value, request.ScopeId.Value, request.PointId.Value, request.DesiredValue,
            request.Unit.Symbol, request.ExpectedRevisionId.Value, request.ExpectedRevisionNumber.Value,
            request.ExpectedGeneration, request.ExpectedManifestFingerprint, request.ExpectedCurrentPosition,
            entry.Value.Value, entry.Quality, entry.Freshness, guard.Version.Value);
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {CommandMigrations.Schema}.prepared_intent
                (intent_id,fingerprint,lease_id,scope_id,point_id,desired_value,unit,revision_id,revision_number,
                 manifest_generation,manifest_fingerprint,current_position,current_value,quality,freshness,
                 safety_version,prepared_at,expires_at)
            VALUES (@intent,@fingerprint,@lease,@scope,@point,@desired,@unit,@revision,@revision_number,
                    @generation,@manifest,@current_position,@current_value,@quality,@freshness,
                    @safety_version,@prepared,@expires)
            ON CONFLICT (intent_id) DO NOTHING;
            """, connection, transaction);
        insert.Parameters.AddWithValue("intent", request.IntentId.Value);
        insert.Parameters.AddWithValue("fingerprint", fingerprint);
        insert.Parameters.AddWithValue("lease", request.LeaseId.Value);
        insert.Parameters.AddWithValue("scope", request.ScopeId.Value);
        insert.Parameters.AddWithValue("point", request.PointId.Value);
        insert.Parameters.AddWithValue("desired", request.DesiredValue);
        insert.Parameters.AddWithValue("unit", request.Unit.Symbol);
        insert.Parameters.AddWithValue("revision", request.ExpectedRevisionId.Value);
        insert.Parameters.AddWithValue("revision_number", checked((long)request.ExpectedRevisionNumber.Value));
        insert.Parameters.AddWithValue("generation", request.ExpectedGeneration);
        insert.Parameters.AddWithValue("manifest", request.ExpectedManifestFingerprint);
        insert.Parameters.AddWithValue("current_position", checked((long)request.ExpectedCurrentPosition));
        insert.Parameters.AddWithValue("current_value", entry.Value.Value);
        insert.Parameters.AddWithValue("quality", (short)entry.Quality);
        insert.Parameters.AddWithValue("freshness", (short)entry.Freshness);
        insert.Parameters.AddWithValue("safety_version", checked((long)guard.Version.Value));
        insert.Parameters.AddWithValue("prepared", now);
        insert.Parameters.AddWithValue("expires", lease.ExpiresAt);
        var inserted = await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (inserted != 1)
        {
            var existing = await ReadIntentAsync(connection, transaction, request.IntentId, token).ConfigureAwait(false);
            return existing is not null && string.Equals(existing.Value.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? Result.Success(existing.Value.Intent)
                : Failure<PreparedCommandIntent>("command.intent_conflict", "Intent identity was reused with different content.");
        }
        var intent = new PreparedCommandIntent(
            request.IntentId, request.LeaseId, request.ScopeId, request.PointId, request.DesiredValue, request.Unit,
            request.ExpectedRevisionId, request.ExpectedRevisionNumber, request.ExpectedGeneration,
            request.ExpectedManifestFingerprint, request.ExpectedCurrentPosition, entry.Value.Value,
            entry.Quality, entry.Freshness, guard.Version, now, lease.ExpiresAt);
        await WriteAuditAsync(connection, transaction, request.LeaseId.Value, request.IntentId.Value,
            authorization.Session, "prepare-intent", "succeeded", now, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(intent);
    }

    private static async Task<ControlLeaseSnapshot?> ReadLeaseAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ControlLeaseId id, bool update,
        CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT scope_id,session_id,subject_id,issued_at,expires_at,revoked_at,revocation_reason,version
            FROM {CommandMigrations.Schema}.control_lease WHERE lease_id=@lease{(update ? " FOR UPDATE" : string.Empty)};
            """, connection, transaction);
        command.Parameters.AddWithValue("lease", id.Value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? new ControlLeaseSnapshot(
            id, RuntimeScopeId.From(reader.GetGuid(0)), SessionId.From(reader.GetGuid(1)), SubjectId.From(reader.GetGuid(2)),
            reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), StateVersion.From(checked((ulong)reader.GetInt64(7)))) : null;
    }

    private static async Task<(StateVersion Version, bool Blocked)> ReadGuardAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeScopeId scopeId, CancellationToken token)
    {
        await using (var ensure = new NpgsqlCommand($"""
            INSERT INTO {CommandMigrations.Schema}.scope_guard
                (scope_id,safety_version,blocked,reason_code,updated_at)
            VALUES (@scope,1,false,'no_block',TIMESTAMPTZ '-infinity') ON CONFLICT DO NOTHING;
            """, connection, transaction))
        { ensure.Parameters.AddWithValue("scope", scopeId.Value); await ensure.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        await using var command = new NpgsqlCommand($"""
            SELECT safety_version,blocked FROM {CommandMigrations.Schema}.scope_guard WHERE scope_id=@scope FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("scope", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        await reader.ReadAsync(token).ConfigureAwait(false);
        return (StateVersion.From(checked((ulong)reader.GetInt64(0))), reader.GetBoolean(1));
    }

    private static async Task<StateVersion> ReadSafetyVersionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeScopeId scopeId, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT safety_version FROM {CommandMigrations.Schema}.scope_guard WHERE scope_id=@scope;", connection, transaction);
        command.Parameters.AddWithValue("scope", scopeId.Value);
        return StateVersion.From(checked((ulong)(long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!));
    }

    private static async Task<string?> ReadReceiptFingerprintAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid factId, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT fingerprint FROM {CommandMigrations.Schema}.safety_fact_receipt WHERE fact_id=@id;", connection, transaction);
        command.Parameters.AddWithValue("id", factId);
        return (string?)await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }

    private static async Task<(string Fingerprint, PreparedCommandIntent Intent)?> ReadIntentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CommandIntentId id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT fingerprint,lease_id,scope_id,point_id,desired_value,unit,revision_id,revision_number,
                   manifest_generation,manifest_fingerprint,current_position,current_value,quality,freshness,
                   safety_version,prepared_at,expires_at
            FROM {CommandMigrations.Schema}.prepared_intent WHERE intent_id=@intent;
            """, connection, transaction);
        command.Parameters.AddWithValue("intent", id.Value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        return (reader.GetString(0), new PreparedCommandIntent(
            id, ControlLeaseId.From(reader.GetGuid(1)), RuntimeScopeId.From(reader.GetGuid(2)), PointId.From(reader.GetGuid(3)),
            reader.GetInt64(4), Unit.FromSymbol(reader.GetString(5)),
            Dispatcher.Configuration.ConfigurationRevisionId.From(reader.GetGuid(6)),
            RevisionNumber.From(checked((ulong)reader.GetInt64(7))), reader.GetInt64(8), reader.GetString(9),
            checked((ulong)reader.GetInt64(10)), reader.GetInt64(11), (DataQuality)reader.GetInt16(12),
            (Freshness)reader.GetInt16(13), StateVersion.From(checked((ulong)reader.GetInt64(14))),
            reader.GetFieldValue<DateTimeOffset>(15), reader.GetFieldValue<DateTimeOffset>(16)));
    }

    private static async Task WriteAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid? lease, Guid? intent,
        SessionSnapshot session, string action, string outcome, DateTimeOffset now, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {CommandMigrations.Schema}.command_audit
                (audit_id,lease_id,intent_id,session_id,subject_id,action,outcome,changed_at)
            VALUES (@audit,@lease,@intent,@session,@subject,@action,@outcome,@now);
            """, connection, transaction);
        command.Parameters.AddWithValue("audit", Guid.NewGuid());
        AddNullableUuid(command, "lease", lease); AddNullableUuid(command, "intent", intent);
        command.Parameters.AddWithValue("session", session.Id.Value);
        command.Parameters.AddWithValue("subject", session.SubjectId.Value);
        command.Parameters.AddWithValue("action", action); command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task SetRoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE {databaseRole};", connection, transaction);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    private DateTimeOffset UtcNow()
    {
        var now = clock.GetUtcNow();
        return now.Offset == TimeSpan.Zero ? now : throw new InvalidOperationException("Command store requires UTC.");
    }
    private static void AddNullableUuid(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = value ?? (object)DBNull.Value });
    private static string Fingerprint(params object?[] values)
    {
        var canonical = string.Join('|', values.Select(value => value switch
        {
            null => "-", DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
            Guid id => id.ToString("N"), Enum item => Convert.ToInt32(item, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));
}
