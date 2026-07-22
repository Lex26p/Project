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

public sealed class CommandExecutionStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;
    private readonly SimulatorCommandCommitHook? commitHook;

    public CommandExecutionStore(
        NpgsqlDataSource dataSource, string databaseRole, IWallClock clock,
        SimulatorCommandCommitHook? commitHook = null)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        if (databaseRole.Any(value => !char.IsAsciiLetterOrDigit(value) && value != '_'))
            throw new ArgumentException("Invalid PostgreSQL role name.", nameof(databaseRole));
        this.databaseRole = databaseRole;
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.commitHook = commitHook;
    }

    public async Task<Result<CommandExecutionSnapshot>> ExecuteAsync(
        AuthorizedMutation authorization, ExecuteSimulatorCommand request,
        ActiveSimulatorManifest activeManifest, CurrentSnapshot current,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(activeManifest);
        ArgumentNullException.ThrowIfNull(current);
        var access = ValidateAccess(authorization, request.ScopeId, request.PointId);
        if (access.IsFailure) return Result.Failure<CommandExecutionSnapshot>(access.Error!);
        var now = UtcNow();

        PreparedCommandIntent intent;
        CommandExecutionSnapshot accepted;
        await using (var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false))
        await using (var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
        {
            await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
            await LockScopeAsync(connection, transaction, request.ScopeId, token).ConfigureAwait(false);
            var existing = await ReadExecutionAsync(connection, transaction, request.ExecutionId, true, token).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.SessionId != authorization.Session.Id || existing.SubjectId != authorization.Session.SubjectId)
                    return Failure<CommandExecutionSnapshot>("command.execution_identity_session", "Execution identity belongs to another session.");
                if (existing.IntentId != request.IntentId || existing.ScopeId != request.ScopeId || existing.PointId != request.PointId)
                    return Failure<CommandExecutionSnapshot>("command.execution_identity_conflict", "Execution identity was reused with different content.");
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return Result.Success(existing with { Disposition = CommandExecutionDisposition.Replayed });
            }

            var prepared = await ReadIntentAsync(connection, transaction, request.IntentId, token).ConfigureAwait(false);
            if (prepared is null)
                return Failure<CommandExecutionSnapshot>("command.intent_not_found", "Prepared command intent was not found.");
            intent = prepared;
            if (intent.ScopeId != request.ScopeId || intent.PointId != request.PointId)
                return Failure<CommandExecutionSnapshot>("command.intent_target_mismatch", "Prepared intent targets another scope or point.");
            var evidence = ValidateEvidence(intent, activeManifest, current);
            if (evidence.IsFailure) return Result.Failure<CommandExecutionSnapshot>(evidence.Error!);
            var lease = await ReadLeaseAsync(connection, transaction, intent.LeaseId, token).ConfigureAwait(false);
            if (lease is null || lease.ScopeId != request.ScopeId)
                return Failure<CommandExecutionSnapshot>("command.lease_not_found", "Bound ControlLease was not found.");
            if (lease.SessionId != authorization.Session.Id || lease.SubjectId != authorization.Session.SubjectId)
                return Failure<CommandExecutionSnapshot>("command.lease_holder_mismatch", "ControlLease belongs to another session.");
            if (lease.RevokedAt is not null || lease.ExpiresAt <= now || intent.ExpiresAt <= now)
                return Failure<CommandExecutionSnapshot>("command.lease_inactive", "ControlLease or prepared intent is revoked or expired.");
            var guard = await ReadGuardAsync(connection, transaction, request.ScopeId, token).ConfigureAwait(false);
            if (guard.Blocked || guard.Version != intent.SafetyVersion)
                return Failure<CommandExecutionSnapshot>("command.safety_stale", "Command safety evidence is blocked or changed after prepare.");
            if (await ReadExecutionByIntentAsync(connection, transaction, request.IntentId, token).ConfigureAwait(false) is not null)
                return Failure<CommandExecutionSnapshot>("command.intent_already_executed", "Prepared intent already has another execution identity.");

            var fingerprint = Fingerprint(request.IntentId.Value, request.ScopeId.Value, request.PointId.Value,
                authorization.Session.Id.Value, authorization.Session.SubjectId.Value);
            accepted = new CommandExecutionSnapshot(
                request.ExecutionId, request.IntentId, intent.LeaseId, request.ScopeId, request.PointId,
                authorization.Session.Id, authorization.Session.SubjectId, CommandExecutionState.Accepted, 0,
                null, null, now, now, null, StateVersion.Initial, CommandExecutionDisposition.Accepted);
            await InsertExecutionAsync(connection, transaction, accepted, fingerprint, token).ConfigureAwait(false);
            await WriteTransitionAsync(connection, transaction, accepted, token).ConfigureAwait(false);
            await WriteAuditAsync(connection, transaction, accepted, "execute-accepted", "accepted", token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }

        var progress = await AdvanceAsync(accepted, CommandExecutionState.InProgress, 50, null, null, token)
            .ConfigureAwait(false);
        try
        {
            var receipt = await ApplySimulatorEffectAsync(progress, intent, activeManifest, token).ConfigureAwait(false);
            return Result.Success(await AdvanceAsync(
                progress, receipt.State, 100, receipt.ResultValue, receipt.RejectionCode, token).ConfigureAwait(false));
        }
        catch (TimeoutException)
        {
            return Result.Success(await AdvanceAsync(
                progress, CommandExecutionState.Unknown, progress.Progress, null, null, token).ConfigureAwait(false));
        }
    }

    public async Task<Result<CommandExecutionSnapshot>> ReconcileAsync(
        AuthorizedMutation authorization, CommandExecutionId executionId,
        RuntimeScopeId scopeId, PointId pointId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        var access = ValidateAccess(authorization, scopeId, pointId);
        if (access.IsFailure) return Result.Failure<CommandExecutionSnapshot>(access.Error!);
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, scopeId, token).ConfigureAwait(false);
        var execution = await ReadExecutionAsync(connection, transaction, executionId, true, token).ConfigureAwait(false);
        if (execution is null || execution.ScopeId != scopeId || execution.PointId != pointId)
            return Failure<CommandExecutionSnapshot>("command.execution_not_found", "Command execution was not found.");
        if (execution.SessionId != authorization.Session.Id || execution.SubjectId != authorization.Session.SubjectId)
            return Failure<CommandExecutionSnapshot>("command.execution_identity_session", "Only the originating session can reconcile this execution identity.");
        var receipt = await ReadReceiptAsync(connection, transaction, executionId, token).ConfigureAwait(false);
        var next = receipt is not null
            ? await AdvanceLockedAsync(connection, transaction, execution, receipt.State, 100,
                receipt.ResultValue, receipt.RejectionCode, "execute-reconciled", token).ConfigureAwait(false)
            : execution.State is CommandExecutionState.Succeeded or CommandExecutionState.Rejected or CommandExecutionState.Unknown
                ? execution
                : await AdvanceLockedAsync(connection, transaction, execution, CommandExecutionState.Unknown,
                    execution.Progress, null, null, "execute-unknown", token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(next with { Disposition = CommandExecutionDisposition.Reconciled });
    }

    public async Task<Result<CommandExecutionSnapshotPage>> ReadSnapshotAsync(
        SessionSnapshot? session, RuntimeScopeId scopeId, CancellationToken token = default)
    {
        var access = SessionAuthorization.AuthorizeAccess(session, CommandPermissions.Execute, clock);
        if (access.IsFailure) return Result.Failure<CommandExecutionSnapshotPage>(access.Error!);
        if (!access.Value.Session.Permissions.Allows(CommandPermissions.ControlScope(scopeId)))
            return Failure<CommandExecutionSnapshotPage>("permission.denied", "Exact scope control permission is required.");
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        var cursor = await ReadCursorAsync(connection, transaction, access.Value.Session.Id, scopeId, token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT execution_id,intent_id,lease_id,scope_id,point_id,session_id,subject_id,state,progress,
                   result_value,rejection_code,accepted_at,updated_at,completed_at,version
            FROM {CommandMigrations.Schema}.command_execution
            WHERE session_id=@session AND scope_id=@scope ORDER BY accepted_at,execution_id LIMIT 128;
            """, connection, transaction);
        command.Parameters.AddWithValue("session", access.Value.Session.Id.Value);
        command.Parameters.AddWithValue("scope", scopeId.Value);
        var rows = new List<CommandExecutionSnapshot>();
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add(MapExecution(reader));
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return Result.Success(new CommandExecutionSnapshotPage(cursor,
            rows.Where(value => access.Value.Session.Permissions.Allows(CommandPermissions.ControlPoint(value.PointId))).ToArray()));
    }

    public async Task<Result<CommandExecutionFeed>> ReadFeedAsync(
        SessionSnapshot? session, RuntimeScopeId scopeId, ulong after, CancellationToken token = default)
    {
        var access = SessionAuthorization.AuthorizeAccess(session, CommandPermissions.Execute, clock);
        if (access.IsFailure) return Result.Failure<CommandExecutionFeed>(access.Error!);
        if (!access.Value.Session.Permissions.Allows(CommandPermissions.ControlScope(scopeId)))
            return Failure<CommandExecutionFeed>("permission.denied", "Exact scope control permission is required.");
        if (after > long.MaxValue) return Failure<CommandExecutionFeed>("command.cursor_invalid", "Command cursor is outside durable range.");
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT t.position,t.execution_id,t.scope_id,t.point_id,t.state,t.progress,t.result_value,
                   t.rejection_code,t.occurred_at,t.version
            FROM {CommandMigrations.Schema}.execution_transition t
            JOIN {CommandMigrations.Schema}.command_execution e ON e.execution_id=t.execution_id
            WHERE e.session_id=@session AND t.scope_id=@scope AND t.position>@after
            ORDER BY t.position LIMIT 128;
            """, connection, transaction);
        command.Parameters.AddWithValue("session", access.Value.Session.Id.Value);
        command.Parameters.AddWithValue("scope", scopeId.Value);
        command.Parameters.AddWithValue("after", checked((long)after));
        var rows = new List<CommandExecutionTransition>();
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add(MapTransition(reader));
        await transaction.CommitAsync(token).ConfigureAwait(false);
        var to = rows.Count == 0 ? after : rows[^1].Position;
        return Result.Success(new CommandExecutionFeed(after, to,
            rows.Where(value => access.Value.Session.Permissions.Allows(CommandPermissions.ControlPoint(value.PointId))).ToArray()));
    }

    private Result ValidateAccess(AuthorizedMutation authorization, RuntimeScopeId scopeId, PointId pointId)
    {
        var validation = SessionAuthorization.ValidateSession(authorization.Session, clock);
        if (validation.IsFailure) return Result.Failure(validation.Error!);
        return authorization.Permission == CommandPermissions.Execute &&
               authorization.Session.Permissions.Allows(CommandPermissions.ControlScope(scopeId)) &&
               authorization.Session.Permissions.Allows(CommandPermissions.ControlPoint(pointId))
            ? Result.Success()
            : Result.Failure(new OperationError(ErrorCode.From("permission.denied"),
                "Execute, exact scope and exact point control permissions are required."));
    }

    private static Result ValidateEvidence(
        PreparedCommandIntent intent, ActiveSimulatorManifest active, CurrentSnapshot current)
    {
        if (active.Configuration.ScopeId != intent.ScopeId || active.Receipt.RevisionId != intent.RevisionId ||
            active.Receipt.RevisionNumber != intent.RevisionNumber || active.Generation != intent.ManifestGeneration ||
            !string.Equals(active.Receipt.ManifestFingerprint, intent.ManifestFingerprint, StringComparison.Ordinal))
            return Failure("command.active_revision_stale", "Active Simulator revision changed after prepare.");
        var point = active.Configuration.Points.SingleOrDefault(value => value.PointId == intent.PointId);
        if (point is null || point.Unit != intent.Unit)
            return Failure("command.target_invalid", "Prepared target is absent from the active Simulator manifest.");
        if (current.ScopeId != intent.ScopeId || current.Position.Value != intent.CurrentPosition)
            return Failure("command.current_stale", "Runtime current position changed after prepare.");
        var entry = current.Entries.SingleOrDefault(value => value.PointId == intent.PointId);
        if (entry is null || entry.Unit != intent.Unit || entry.Value.Value != intent.CurrentValue ||
            entry.Quality != DataQuality.Good || entry.Freshness != Freshness.Fresh)
            return Failure("command.current_stale", "Target current value, quality or freshness changed after prepare.");
        return Result.Success();
    }

    private async Task<CommandExecutionSnapshot> AdvanceAsync(
        CommandExecutionSnapshot execution, CommandExecutionState state, byte progress,
        long? resultValue, string? rejectionCode, CancellationToken token)
    {
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, execution.ScopeId, token).ConfigureAwait(false);
        var current = await ReadExecutionAsync(connection, transaction, execution.ExecutionId, true, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Command execution disappeared.");
        var next = await AdvanceLockedAsync(connection, transaction, current, state, progress,
            resultValue, rejectionCode, $"execute-{state.ToString().ToLowerInvariant()}", token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return next;
    }

    private async Task<CommandExecutionSnapshot> AdvanceLockedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CommandExecutionSnapshot current,
        CommandExecutionState state, byte progress, long? resultValue, string? rejectionCode,
        string auditAction, CancellationToken token)
    {
        if (current.State is CommandExecutionState.Succeeded or CommandExecutionState.Rejected)
            return current;
        if (current.State == state && current.Progress == progress && current.ResultValue == resultValue &&
            string.Equals(current.RejectionCode, rejectionCode, StringComparison.Ordinal)) return current;
        var now = UtcNow();
        var terminal = state is CommandExecutionState.Succeeded or CommandExecutionState.Rejected;
        var next = current with
        {
            State = state, Progress = progress, ResultValue = resultValue, RejectionCode = rejectionCode,
            UpdatedAt = now, CompletedAt = terminal ? now : null, Version = current.Version.Next(),
        };
        await using (var update = new NpgsqlCommand($"""
            UPDATE {CommandMigrations.Schema}.command_execution
            SET state=@state,progress=@progress,result_value=@result,rejection_code=@rejection,
                updated_at=@updated,completed_at=@completed,version=@version
            WHERE execution_id=@execution AND version=@expected;
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("execution", current.ExecutionId.Value);
            update.Parameters.AddWithValue("state", (short)state);
            update.Parameters.AddWithValue("progress", (short)progress);
            AddNullableInt64(update, "result", resultValue);
            AddNullableText(update, "rejection", rejectionCode);
            update.Parameters.AddWithValue("updated", now);
            AddNullableTimestamp(update, "completed", next.CompletedAt);
            update.Parameters.AddWithValue("version", checked((long)next.Version.Value));
            update.Parameters.AddWithValue("expected", checked((long)current.Version.Value));
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Concurrent command execution transition was not serialized.");
        }
        await WriteTransitionAsync(connection, transaction, next, token).ConfigureAwait(false);
        await WriteAuditAsync(connection, transaction, next, auditAction, state.ToString(), token).ConfigureAwait(false);
        return next;
    }

    private async Task<SimulatorReceipt> ApplySimulatorEffectAsync(
        CommandExecutionSnapshot execution, PreparedCommandIntent intent,
        ActiveSimulatorManifest active, CancellationToken token)
    {
        var point = active.Configuration.Points.Single(value => value.PointId == intent.PointId);
        var allowed = (decimal)intent.DesiredValue >= (decimal)point.Baseline - point.Amplitude &&
                      (decimal)intent.DesiredValue <= (decimal)point.Baseline + point.Amplitude;
        var receipt = new SimulatorReceipt(
            allowed ? CommandExecutionState.Succeeded : CommandExecutionState.Rejected,
            allowed ? intent.DesiredValue : null, allowed ? null : "simulator.value_out_of_range");
        var fingerprint = Fingerprint(execution.ExecutionId.Value, intent.IntentId.Value, intent.DesiredValue,
            intent.Unit.Symbol, intent.ManifestGeneration, intent.ManifestFingerprint, receipt.State,
            receipt.ResultValue, receipt.RejectionCode);
        await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, token).ConfigureAwait(false);
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {CommandMigrations.Schema}.simulator_execution_receipt
                (execution_id,fingerprint,outcome,result_value,rejection_code,accepted_at)
            VALUES (@execution,@fingerprint,@outcome,@result,@rejection,@accepted)
            ON CONFLICT (execution_id) DO NOTHING;
            """, connection, transaction);
        insert.Parameters.AddWithValue("execution", execution.ExecutionId.Value);
        insert.Parameters.AddWithValue("fingerprint", fingerprint);
        insert.Parameters.AddWithValue("outcome", (short)receipt.State);
        AddNullableInt64(insert, "result", receipt.ResultValue);
        AddNullableText(insert, "rejection", receipt.RejectionCode);
        insert.Parameters.AddWithValue("accepted", UtcNow());
        if (await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
        {
            var existing = await ReadReceiptWithFingerprintAsync(connection, transaction, execution.ExecutionId, token)
                .ConfigureAwait(false);
            if (existing is null || !string.Equals(existing.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("Simulator command identity was reused with another effect.");
            receipt = existing.Value.Receipt;
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        commitHook?.AfterCommit(execution.ExecutionId);
        return receipt;
    }

    private static async Task InsertExecutionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CommandExecutionSnapshot execution,
        string fingerprint, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {CommandMigrations.Schema}.command_execution
                (execution_id,intent_id,request_fingerprint,lease_id,scope_id,point_id,session_id,subject_id,
                 state,progress,result_value,rejection_code,accepted_at,updated_at,completed_at,version)
            VALUES (@execution,@intent,@fingerprint,@lease,@scope,@point,@session,@subject,
                    @state,@progress,NULL,NULL,@accepted,@updated,NULL,@version);
            """, connection, transaction);
        command.Parameters.AddWithValue("execution", execution.ExecutionId.Value);
        command.Parameters.AddWithValue("intent", execution.IntentId.Value);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        command.Parameters.AddWithValue("lease", execution.LeaseId.Value);
        command.Parameters.AddWithValue("scope", execution.ScopeId.Value);
        command.Parameters.AddWithValue("point", execution.PointId.Value);
        command.Parameters.AddWithValue("session", execution.SessionId.Value);
        command.Parameters.AddWithValue("subject", execution.SubjectId.Value);
        command.Parameters.AddWithValue("state", (short)execution.State);
        command.Parameters.AddWithValue("progress", (short)execution.Progress);
        command.Parameters.AddWithValue("accepted", execution.AcceptedAt);
        command.Parameters.AddWithValue("updated", execution.UpdatedAt);
        command.Parameters.AddWithValue("version", checked((long)execution.Version.Value));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<PreparedCommandIntent?> ReadIntentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CommandIntentId id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT lease_id,scope_id,point_id,desired_value,unit,revision_id,revision_number,
                   manifest_generation,manifest_fingerprint,current_position,current_value,quality,freshness,
                   safety_version,prepared_at,expires_at
            FROM {CommandMigrations.Schema}.prepared_intent WHERE intent_id=@intent;
            """, connection, transaction);
        command.Parameters.AddWithValue("intent", id.Value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? new PreparedCommandIntent(
            id, ControlLeaseId.From(reader.GetGuid(0)), RuntimeScopeId.From(reader.GetGuid(1)), PointId.From(reader.GetGuid(2)),
            reader.GetInt64(3), Unit.FromSymbol(reader.GetString(4)),
            Dispatcher.Configuration.ConfigurationRevisionId.From(reader.GetGuid(5)),
            RevisionNumber.From(checked((ulong)reader.GetInt64(6))), reader.GetInt64(7), reader.GetString(8),
            checked((ulong)reader.GetInt64(9)), reader.GetInt64(10), (DataQuality)reader.GetInt16(11),
            (Freshness)reader.GetInt16(12), StateVersion.From(checked((ulong)reader.GetInt64(13))),
            reader.GetFieldValue<DateTimeOffset>(14), reader.GetFieldValue<DateTimeOffset>(15)) : null;
    }

    private static async Task<LeaseRow?> ReadLeaseAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ControlLeaseId id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT scope_id,session_id,subject_id,expires_at,revoked_at
            FROM {CommandMigrations.Schema}.control_lease WHERE lease_id=@lease FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("lease", id.Value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? new LeaseRow(
            RuntimeScopeId.From(reader.GetGuid(0)), SessionId.From(reader.GetGuid(1)), SubjectId.From(reader.GetGuid(2)),
            reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)) : null;
    }

    private static async Task<(StateVersion Version, bool Blocked)> ReadGuardAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeScopeId scopeId, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT safety_version,blocked FROM {CommandMigrations.Schema}.scope_guard
            WHERE scope_id=@scope FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("scope", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException("Prepared command safety guard is missing.");
        return (StateVersion.From(checked((ulong)reader.GetInt64(0))), reader.GetBoolean(1));
    }

    private static async Task<CommandExecutionSnapshot?> ReadExecutionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CommandExecutionId id, bool update,
        CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT execution_id,intent_id,lease_id,scope_id,point_id,session_id,subject_id,state,progress,
                   result_value,rejection_code,accepted_at,updated_at,completed_at,version
            FROM {CommandMigrations.Schema}.command_execution WHERE execution_id=@execution{(update ? " FOR UPDATE" : string.Empty)};
            """, connection, transaction);
        command.Parameters.AddWithValue("execution", id.Value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? MapExecution(reader) : null;
    }

    private static async Task<Guid?> ReadExecutionByIntentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CommandIntentId id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT execution_id FROM {CommandMigrations.Schema}.command_execution WHERE intent_id=@intent;",
            connection, transaction);
        command.Parameters.AddWithValue("intent", id.Value);
        return (Guid?)await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }

    private static async Task<SimulatorReceipt?> ReadReceiptAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CommandExecutionId id, CancellationToken token) =>
        (await ReadReceiptWithFingerprintAsync(connection, transaction, id, token).ConfigureAwait(false))?.Receipt;

    private static async Task<(string Fingerprint, SimulatorReceipt Receipt)?> ReadReceiptWithFingerprintAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CommandExecutionId id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT fingerprint,outcome,result_value,rejection_code
            FROM {CommandMigrations.Schema}.simulator_execution_receipt WHERE execution_id=@execution;
            """, connection, transaction);
        command.Parameters.AddWithValue("execution", id.Value);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? (reader.GetString(0), new SimulatorReceipt((CommandExecutionState)reader.GetInt16(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3)))
            : null;
    }

    private static CommandExecutionSnapshot MapExecution(NpgsqlDataReader reader) => new(
        CommandExecutionId.From(reader.GetGuid(0)), CommandIntentId.From(reader.GetGuid(1)),
        ControlLeaseId.From(reader.GetGuid(2)), RuntimeScopeId.From(reader.GetGuid(3)), PointId.From(reader.GetGuid(4)),
        SessionId.From(reader.GetGuid(5)), SubjectId.From(reader.GetGuid(6)),
        (CommandExecutionState)reader.GetInt16(7), checked((byte)reader.GetInt16(8)),
        reader.IsDBNull(9) ? null : reader.GetInt64(9), reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.GetFieldValue<DateTimeOffset>(11), reader.GetFieldValue<DateTimeOffset>(12),
        reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
        StateVersion.From(checked((ulong)reader.GetInt64(14))), CommandExecutionDisposition.Accepted);

    private static CommandExecutionTransition MapTransition(NpgsqlDataReader reader) => new(
        checked((ulong)reader.GetInt64(0)), CommandExecutionId.From(reader.GetGuid(1)),
        RuntimeScopeId.From(reader.GetGuid(2)), PointId.From(reader.GetGuid(3)),
        (CommandExecutionState)reader.GetInt16(4), checked((byte)reader.GetInt16(5)),
        reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetFieldValue<DateTimeOffset>(8), StateVersion.From(checked((ulong)reader.GetInt64(9))));

    private static async Task WriteTransitionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CommandExecutionSnapshot execution,
        CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {CommandMigrations.Schema}.execution_transition
                (execution_id,scope_id,point_id,state,progress,result_value,rejection_code,occurred_at,version)
            VALUES (@execution,@scope,@point,@state,@progress,@result,@rejection,@occurred,@version);
            """, connection, transaction);
        command.Parameters.AddWithValue("execution", execution.ExecutionId.Value);
        command.Parameters.AddWithValue("scope", execution.ScopeId.Value);
        command.Parameters.AddWithValue("point", execution.PointId.Value);
        command.Parameters.AddWithValue("state", (short)execution.State);
        command.Parameters.AddWithValue("progress", (short)execution.Progress);
        AddNullableInt64(command, "result", execution.ResultValue);
        AddNullableText(command, "rejection", execution.RejectionCode);
        command.Parameters.AddWithValue("occurred", execution.UpdatedAt);
        command.Parameters.AddWithValue("version", checked((long)execution.Version.Value));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task WriteAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CommandExecutionSnapshot execution,
        string action, string outcome, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {CommandMigrations.Schema}.command_audit
                (audit_id,lease_id,intent_id,session_id,subject_id,action,outcome,changed_at,execution_id)
            VALUES (@audit,@lease,@intent,@session,@subject,@action,@outcome,@changed,@execution);
            """, connection, transaction);
        command.Parameters.AddWithValue("audit", Guid.NewGuid());
        command.Parameters.AddWithValue("lease", execution.LeaseId.Value);
        command.Parameters.AddWithValue("intent", execution.IntentId.Value);
        command.Parameters.AddWithValue("session", execution.SessionId.Value);
        command.Parameters.AddWithValue("subject", execution.SubjectId.Value);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("changed", execution.UpdatedAt);
        command.Parameters.AddWithValue("execution", execution.ExecutionId.Value);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<ulong> ReadCursorAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, SessionId sessionId,
        RuntimeScopeId scopeId, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT COALESCE(max(t.position),0) FROM {CommandMigrations.Schema}.execution_transition t
            JOIN {CommandMigrations.Schema}.command_execution e ON e.execution_id=t.execution_id
            WHERE e.session_id=@session AND t.scope_id=@scope;
            """, connection, transaction);
        command.Parameters.AddWithValue("session", sessionId.Value);
        command.Parameters.AddWithValue("scope", scopeId.Value);
        return checked((ulong)(long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!);
    }

    private static async Task LockScopeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeScopeId scopeId, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@scope, 38));", connection, transaction);
        command.Parameters.AddWithValue("scope", scopeId.Value.ToString("N"));
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
        return now.Offset == TimeSpan.Zero ? now : throw new InvalidOperationException("Command execution requires UTC.");
    }

    private static void AddNullableInt64(NpgsqlCommand command, string name, long? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Bigint) { Value = value ?? (object)DBNull.Value });
    private static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = value ?? (object)DBNull.Value });
    private static void AddNullableTimestamp(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = value ?? (object)DBNull.Value });
    private static string Fingerprint(params object?[] values)
    {
        var canonical = string.Join('|', values.Select(value => value switch
        {
            null => "-", Guid id => id.ToString("N"), Enum item => Convert.ToInt32(item, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));
    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));

    private sealed record LeaseRow(
        RuntimeScopeId ScopeId, SessionId SessionId, SubjectId SubjectId,
        DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);
    private sealed record SimulatorReceipt(
        CommandExecutionState State, long? ResultValue, string? RejectionCode);
}
