using System.Text.RegularExpressions;
using Dispatcher.Configuration;
using Dispatcher.Facilities;
using Dispatcher.Semantics;
using Npgsql;

namespace Dispatcher.Simulator;

public sealed partial class SimulatorRuntimeStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;
    private readonly SimulatorActivationCommitHook? activationHook;

    public SimulatorRuntimeStore(
        NpgsqlDataSource dataSource,
        string databaseRole,
        IWallClock clock,
        SimulatorActivationCommitHook? activationHook = null)
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
        this.activationHook = activationHook;
    }

    public async Task<Result<SimulatorManifestReceipt>> ReceiveAsync(
        SimulatorManifestDelivery delivery,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ValidateWorker(workerId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, delivery.ScopeId, cancellationToken).ConfigureAwait(false);
        await EnsureScopeAsync(connection, transaction, delivery.ScopeId, cancellationToken).ConfigureAwait(false);
        var state = (await ReadScopeAsync(connection, transaction, delivery.ScopeId, true, cancellationToken)
            .ConfigureAwait(false))!;
        var existing = await ReadManifestAsync(
            connection,
            transaction,
            delivery.RevisionId,
            false,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (Matches(existing, delivery))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Result.Success(existing.Receipt);
            }

            return Failure<SimulatorManifestReceipt>(
                "simulator.manifest_conflict",
                "Revision identity was already received with different content.");
        }

        var expected = checked(state.LastReceivedRevisionNumber + 1);
        if (delivery.RevisionNumber.Value != checked((ulong)expected))
        {
            return Failure<SimulatorManifestReceipt>(
                "simulator.manifest_reordered",
                $"Expected revision number {expected} before receiving this manifest.");
        }

        var now = clock.GetUtcNow();
        await using (var insert = new NpgsqlCommand(
                         $"""
                         INSERT INTO {SimulatorRuntimeMigrations.Schema}.manifest
                             (revision_id, scope_id, revision_number, source_revision_id,
                              manifest, manifest_fingerprint, dependency_fingerprint, status, received_at)
                         VALUES
                             (@revision_id, @scope_id, @revision_number, @source_revision_id,
                              @manifest, @manifest_fingerprint, @dependency_fingerprint, 1, @received_at);
                         UPDATE {SimulatorRuntimeMigrations.Schema}.scope_state
                         SET last_received_revision_number = @revision_number WHERE scope_id = @scope_id;
                         """,
                         connection,
                         transaction))
        {
            insert.Parameters.AddWithValue("revision_id", delivery.RevisionId.Value);
            insert.Parameters.AddWithValue("scope_id", delivery.ScopeId.Value);
            insert.Parameters.AddWithValue("revision_number", checked((long)delivery.RevisionNumber.Value));
            insert.Parameters.AddWithValue(
                "source_revision_id",
                (object?)delivery.SourceRevisionId?.Value ?? DBNull.Value);
            insert.Parameters.AddWithValue("manifest", delivery.ManifestJson);
            insert.Parameters.AddWithValue("manifest_fingerprint", delivery.ManifestFingerprint);
            insert.Parameters.AddWithValue("dependency_fingerprint", delivery.DependencyFingerprint);
            insert.Parameters.AddWithValue("received_at", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var receipt = new SimulatorManifestReceipt(
            delivery.RevisionId,
            delivery.ScopeId,
            delivery.RevisionNumber,
            delivery.SourceRevisionId,
            delivery.ManifestFingerprint,
            delivery.DependencyFingerprint,
            SimulatorManifestStatus.Received,
            null,
            null,
            now,
            null,
            null);
        await InsertAuditAsync(
            connection,
            transaction,
            receipt,
            workerId,
            "manifest.receive",
            state.ActiveGeneration,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(receipt);
    }

    public async Task<Result<SimulatorManifestReceipt>> ValidateAsync(
        FacilityScopeId scopeId,
        ConfigurationRevisionId revisionId,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorker(workerId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        var manifest = await ReadManifestAsync(connection, transaction, revisionId, true, cancellationToken)
            .ConfigureAwait(false);
        if (manifest is null || manifest.Receipt.ScopeId != scopeId)
        {
            return Failure<SimulatorManifestReceipt>("simulator.manifest_not_found", "Simulator manifest was not received.");
        }

        if (manifest.Receipt.Status != SimulatorManifestStatus.Received)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(manifest.Receipt);
        }

        var validation = SimulatorManifestValidator.Validate(
            scopeId,
            manifest.ManifestJson,
            manifest.Receipt.ManifestFingerprint);
        var now = clock.GetUtcNow();
        var status = validation.IsSuccess ? SimulatorManifestStatus.Validated : SimulatorManifestStatus.Rejected;
        var receipt = manifest.Receipt with
        {
            Status = status,
            RejectionCode = validation.Error?.Code.Value,
            RejectionMessage = validation.Error?.Message,
            ValidatedAt = now,
        };
        await using (var update = new NpgsqlCommand(
                         $"""
                         UPDATE {SimulatorRuntimeMigrations.Schema}.manifest
                         SET status = @status, rejection_code = @rejection_code,
                             rejection_message = @rejection_message, validated_at = @validated_at
                         WHERE revision_id = @revision_id;
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("status", (short)status);
            update.Parameters.AddWithValue("rejection_code", (object?)receipt.RejectionCode ?? DBNull.Value);
            update.Parameters.AddWithValue("rejection_message", (object?)receipt.RejectionMessage ?? DBNull.Value);
            update.Parameters.AddWithValue("validated_at", now);
            update.Parameters.AddWithValue("revision_id", revisionId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var state = (await ReadScopeAsync(connection, transaction, scopeId, false, cancellationToken)
            .ConfigureAwait(false))!;
        await InsertAuditAsync(
            connection,
            transaction,
            receipt,
            workerId,
            validation.IsSuccess ? "manifest.validate" : "manifest.reject",
            state.ActiveGeneration,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(receipt);
    }

    public async Task<Result<ActiveSimulatorManifest>> ActivateAsync(
        FacilityScopeId scopeId,
        ConfigurationRevisionId revisionId,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorker(workerId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        var state = await ReadScopeAsync(connection, transaction, scopeId, true, cancellationToken).ConfigureAwait(false);
        var manifest = await ReadManifestAsync(connection, transaction, revisionId, true, cancellationToken)
            .ConfigureAwait(false);
        if (state is null || manifest is null || manifest.Receipt.ScopeId != scopeId ||
            manifest.Receipt.Status is not (SimulatorManifestStatus.Validated or SimulatorManifestStatus.Activated))
        {
            return Failure<ActiveSimulatorManifest>(
                "simulator.activation_not_ready",
                "Only a validated whole-scope manifest can be activated.");
        }

        if (state.ActiveRevisionId == revisionId)
        {
            var restored = ToActive(manifest, state.ActiveGeneration);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return restored;
        }

        if (state.ActiveRevisionId is not null)
        {
            var current = (await ReadManifestAsync(
                connection,
                transaction,
                state.ActiveRevisionId.Value,
                false,
                cancellationToken).ConfigureAwait(false))!;
            if (manifest.Receipt.RevisionNumber <= current.Receipt.RevisionNumber)
            {
                return Failure<ActiveSimulatorManifest>(
                    "simulator.activation_stale",
                    "An older or reused revision cannot replace the active manifest.");
            }
        }

        var validation = SimulatorManifestValidator.Validate(
            scopeId,
            manifest.ManifestJson,
            manifest.Receipt.ManifestFingerprint);
        if (validation.IsFailure)
        {
            return Result.Failure<ActiveSimulatorManifest>(validation.Error!);
        }

        var now = clock.GetUtcNow();
        var generation = checked(state.ActiveGeneration + 1);
        var receipt = manifest.Receipt with
        {
            Status = SimulatorManifestStatus.Activated,
            ActivatedAt = now,
        };
        await using (var update = new NpgsqlCommand(
                         $"""
                         UPDATE {SimulatorRuntimeMigrations.Schema}.manifest
                         SET status = 4, activated_at = @activated_at WHERE revision_id = @revision_id;
                         UPDATE {SimulatorRuntimeMigrations.Schema}.scope_state
                         SET active_revision_id = @revision_id, active_generation = @generation
                         WHERE scope_id = @scope_id;
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("activated_at", now);
            update.Parameters.AddWithValue("revision_id", revisionId.Value);
            update.Parameters.AddWithValue("generation", generation);
            update.Parameters.AddWithValue("scope_id", scopeId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            receipt,
            workerId,
            "manifest.activate",
            generation,
            cancellationToken).ConfigureAwait(false);
        if (activationHook is not null)
        {
            await activationHook.BeforeCommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new ActiveSimulatorManifest(receipt, validation.Value, generation));
    }

    public async Task<Result<ActiveSimulatorManifest>> ReadActiveAsync(
        FacilityScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var state = await ReadScopeAsync(connection, transaction, scopeId, false, cancellationToken).ConfigureAwait(false);
        if (state?.ActiveRevisionId is null)
        {
            return Failure<ActiveSimulatorManifest>("simulator.active_not_found", "No active Simulator manifest exists.");
        }

        var manifest = (await ReadManifestAsync(
            connection,
            transaction,
            state.ActiveRevisionId.Value,
            false,
            cancellationToken).ConfigureAwait(false))!;
        var result = ToActive(manifest, state.ActiveGeneration);
        if (result.IsFailure)
        {
            return result;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static Result<ActiveSimulatorManifest> ToActive(ManifestRecord manifest, long generation)
    {
        var validation = SimulatorManifestValidator.Validate(
            manifest.Receipt.ScopeId,
            manifest.ManifestJson,
            manifest.Receipt.ManifestFingerprint);
        return validation.IsSuccess
            ? Result.Success(new ActiveSimulatorManifest(manifest.Receipt, validation.Value, generation))
            : Result.Failure<ActiveSimulatorManifest>(validation.Error!);
    }

    private static bool Matches(ManifestRecord existing, SimulatorManifestDelivery delivery) =>
        existing.Receipt.ScopeId == delivery.ScopeId &&
        existing.Receipt.RevisionNumber == delivery.RevisionNumber &&
        existing.Receipt.SourceRevisionId == delivery.SourceRevisionId &&
        existing.Receipt.ManifestFingerprint == delivery.ManifestFingerprint &&
        existing.Receipt.DependencyFingerprint == delivery.DependencyFingerprint;

    private static async Task EnsureScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FacilityScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"INSERT INTO {SimulatorRuntimeMigrations.Schema}.scope_state (scope_id) VALUES (@scope_id) ON CONFLICT DO NOTHING;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ScopeState?> ReadScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FacilityScopeId scopeId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT last_received_revision_number, active_revision_id, active_generation
            FROM {SimulatorRuntimeMigrations.Schema}.scope_state
            WHERE scope_id = @scope_id {(forUpdate ? "FOR UPDATE" : string.Empty)};
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ScopeState(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : ConfigurationRevisionId.From(reader.GetGuid(1)),
                reader.GetInt64(2))
            : null;
    }

    private static async Task<ManifestRecord?> ReadManifestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ConfigurationRevisionId revisionId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT revision_id, scope_id, revision_number, source_revision_id, manifest,
                   manifest_fingerprint, dependency_fingerprint, status, rejection_code,
                   rejection_message, received_at, validated_at, activated_at
            FROM {SimulatorRuntimeMigrations.Schema}.manifest
            WHERE revision_id = @revision_id {(forUpdate ? "FOR UPDATE" : string.Empty)};
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision_id", revisionId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ManifestRecord(
            new SimulatorManifestReceipt(
                ConfigurationRevisionId.From(reader.GetGuid(0)),
                FacilityScopeId.From(reader.GetGuid(1)),
                RevisionNumber.From(checked((ulong)reader.GetInt64(2))),
                reader.IsDBNull(3) ? null : ConfigurationRevisionId.From(reader.GetGuid(3)),
                reader.GetString(5),
                reader.GetString(6),
                (SimulatorManifestStatus)reader.GetInt16(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetFieldValue<DateTimeOffset>(10),
                NullableTimestamp(reader, 11),
                NullableTimestamp(reader, 12)),
            reader.GetString(4));
    }

    private async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SimulatorManifestReceipt receipt,
        string workerId,
        string action,
        long generation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {SimulatorRuntimeMigrations.Schema}.activation_audit
                (audit_id, scope_id, revision_id, worker_id, action, active_generation, changed_at)
            VALUES (@audit_id, @scope_id, @revision_id, @worker_id, @action, @generation, @changed_at);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("audit_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("scope_id", receipt.ScopeId.Value);
        command.Parameters.AddWithValue("revision_id", receipt.RevisionId.Value);
        command.Parameters.AddWithValue("worker_id", workerId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("generation", generation);
        command.Parameters.AddWithValue("changed_at", clock.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task LockScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FacilityScopeId scopeId,
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

    private static DateTimeOffset? NullableTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static void ValidateWorker(string workerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (workerId.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }
    }

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));

    private sealed record ScopeState(
        long LastReceivedRevisionNumber,
        ConfigurationRevisionId? ActiveRevisionId,
        long ActiveGeneration);

    private sealed record ManifestRecord(SimulatorManifestReceipt Receipt, string ManifestJson);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
