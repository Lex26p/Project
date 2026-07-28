using System.Text.Json;
using System.Text.RegularExpressions;
using Dispatcher.Facilities;
using Dispatcher.Semantics;
using Npgsql;

namespace Dispatcher.Configuration;

public sealed partial class ConfigurationWorkloadDeploymentStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;

    public ConfigurationWorkloadDeploymentStore(
        NpgsqlDataSource dataSource,
        string databaseRole,
        IWallClock clock)
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

    public async Task<Result<ConfigurationWorkloadClaim>> ClaimNextAsync(
        FacilityScopeId scopeId,
        string workloadIdentity,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(workloadIdentity);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        var now = clock.GetUtcNow();
        var leaseToken = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            WITH candidate AS (
                SELECT job.job_id
                FROM {ConfigurationMigrations.Schema}.distribution_job job
                JOIN {ConfigurationMigrations.Schema}.scope_state state
                  ON state.scope_id = job.scope_id
                 AND state.published_revision_id = job.revision_id
                WHERE job.scope_id = @scope_id
                  AND job.completed_at IS NULL
                  AND job.available_at <= @now
                  AND (job.lease_until IS NULL OR job.lease_until <= @now)
                ORDER BY job.available_at, job.job_id
                FOR UPDATE OF job SKIP LOCKED
                LIMIT 1
            )
            UPDATE {ConfigurationMigrations.Schema}.distribution_job job
            SET claimed_by = @worker_id,
                lease_until = @lease_until,
                lease_token = @lease_token,
                attempts = attempts + 1
            FROM candidate
            WHERE job.job_id = candidate.job_id
            RETURNING job.job_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("worker_id", workloadIdentity);
        command.Parameters.AddWithValue("lease_until", now.Add(leaseDuration));
        command.Parameters.AddWithValue("lease_token", leaseToken);
        var claimedJob = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (claimedJob is not Guid jobId)
        {
            return Failure<ConfigurationWorkloadClaim>(
                "configuration.workload_job_not_available",
                "No workload deployment is available.");
        }

        var claim = await ReadClaimAsync(
            connection,
            transaction,
            DistributionJobId.From(jobId),
            cancellationToken).ConfigureAwait(false);
        await InsertEventAsync(
            connection,
            transaction,
            claim!,
            workloadIdentity,
            "workload.claim",
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(claim!);
    }

    public async Task<Result<ConfigurationWorkloadClaim>> ConfirmLeaseAsync(
        ConfigurationWorkloadClaim expected,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {ConfigurationMigrations.Schema}.distribution_job
            SET lease_until = @lease_until
            WHERE job_id = @job_id
              AND completed_at IS NULL
              AND claimed_by = @worker_id
              AND lease_token = @lease_token
              AND lease_until > @now;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("lease_until", now.Add(leaseDuration));
        command.Parameters.AddWithValue("job_id", expected.JobId.Value);
        command.Parameters.AddWithValue("worker_id", expected.ClaimedBy);
        command.Parameters.AddWithValue("lease_token", expected.LeaseToken);
        command.Parameters.AddWithValue("now", now);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            return InvalidLease<ConfigurationWorkloadClaim>();
        }

        var claim = await ReadClaimAsync(
            connection,
            transaction,
            expected.JobId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(claim!);
    }

    public async Task<Result<ConfigurationWorkloadClaim>> MarkPreparedAsync(
        ConfigurationWorkloadClaim expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(
            connection,
            transaction,
            expected.Revision.ScopeId,
            cancellationToken).ConfigureAwait(false);
        var current = await LockClaimAsync(connection, transaction, expected, now, cancellationToken)
            .ConfigureAwait(false);
        if (current.IsFailure)
        {
            return Result.Failure<ConfigurationWorkloadClaim>(current.Error!);
        }

        if (current.Value.PreparedAt is null)
        {
            await using var update = new NpgsqlCommand(
                $"""
                UPDATE {ConfigurationMigrations.Schema}.distribution_job
                SET prepared_at = @now
                WHERE job_id = @job_id;
                UPDATE {ConfigurationMigrations.Schema}.revision
                SET distributed_at = COALESCE(distributed_at, @now),
                    version = CASE WHEN distributed_at IS NULL THEN version + 1 ELSE version END
                WHERE revision_id = @revision_id;
                UPDATE {ConfigurationMigrations.Schema}.scope_state
                SET distributed_revision_id = @revision_id,
                    pending_activation_revision_id = @revision_id,
                    version = CASE
                        WHEN distributed_revision_id = @revision_id THEN version
                        ELSE version + 1
                    END
                WHERE scope_id = @scope_id
                  AND published_revision_id = @revision_id;
                """,
                connection,
                transaction);
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("job_id", expected.JobId.Value);
            update.Parameters.AddWithValue("revision_id", expected.Revision.RevisionId.Value);
            update.Parameters.AddWithValue("scope_id", expected.Revision.ScopeId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            current = Result.Success((await ReadClaimAsync(
                connection,
                transaction,
                expected.JobId,
                cancellationToken).ConfigureAwait(false))!);
            await InsertEventAsync(
                connection,
                transaction,
                current.Value,
                expected.ClaimedBy,
                "workload.prepare",
                null,
                null,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return current;
    }

    public Task<Result<ConfigurationWorkloadClaim>> RecordSwitchAsync(
        ConfigurationWorkloadClaim expected,
        long runtimeGeneration,
        RevisionNumber alarmDefinitionEpoch,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runtimeGeneration);
        if (!alarmDefinitionEpoch.IsDefined)
        {
            throw new ArgumentOutOfRangeException(nameof(alarmDefinitionEpoch));
        }

        return RecordSwitchCoreAsync(
            expected,
            runtimeGeneration,
            alarmDefinitionEpoch,
            cancellationToken);
    }

    public async Task<Result<ConfigurationWorkloadActivation>> AcknowledgeAsync(
        ConfigurationWorkloadClaim expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(
            connection,
            transaction,
            expected.Revision.ScopeId,
            cancellationToken).ConfigureAwait(false);
        var current = await LockClaimAsync(connection, transaction, expected, now, cancellationToken)
            .ConfigureAwait(false);
        if (current.IsFailure)
        {
            return Result.Failure<ConfigurationWorkloadActivation>(current.Error!);
        }

        if (current.Value.SwitchedAt is null ||
            current.Value.RuntimeGeneration is null ||
            current.Value.AlarmDefinitionEpoch is null)
        {
            return Failure<ConfigurationWorkloadActivation>(
                "configuration.workload_not_switched",
                "Only a switched whole-scope deployment can be acknowledged.");
        }

        await using var update = new NpgsqlCommand(
            $"""
            UPDATE {ConfigurationMigrations.Schema}.distribution_job
            SET acknowledged_at = COALESCE(acknowledged_at, @now),
                completed_at = COALESCE(completed_at, @now),
                outcome_code = 'activated',
                outcome_message = 'Whole-scope runtime activation acknowledged.'
            WHERE job_id = @job_id;
            UPDATE {ConfigurationMigrations.Schema}.revision
            SET activated_at = COALESCE(activated_at, @now),
                version = CASE WHEN activated_at IS NULL THEN version + 1 ELSE version END
            WHERE revision_id = @revision_id;
            UPDATE {ConfigurationMigrations.Schema}.scope_state
            SET activated_revision_id = @revision_id,
                pending_activation_revision_id = NULL,
                version = CASE
                    WHEN activated_revision_id = @revision_id THEN version
                    ELSE version + 1
                END
            WHERE scope_id = @scope_id
              AND published_revision_id = @revision_id
              AND distributed_revision_id = @revision_id;
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("now", now);
        update.Parameters.AddWithValue("job_id", expected.JobId.Value);
        update.Parameters.AddWithValue("revision_id", expected.Revision.RevisionId.Value);
        update.Parameters.AddWithValue("scope_id", expected.Revision.ScopeId.Value);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var acknowledged = (await ReadClaimAsync(
            connection,
            transaction,
            expected.JobId,
            cancellationToken).ConfigureAwait(false))!;
        await InsertEventAsync(
            connection,
            transaction,
            acknowledged,
            expected.ClaimedBy,
            "workload.acknowledge",
            "activated",
            "Whole-scope runtime activation acknowledged.",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new ConfigurationWorkloadActivation(
            acknowledged.Revision,
            acknowledged.RuntimeGeneration!.Value,
            acknowledged.AlarmDefinitionEpoch!.Value,
            now));
    }

    public async Task<Result> RejectAsync(
        ConfigurationWorkloadClaim expected,
        string outcomeCode,
        string safeMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ValidateOutcome(outcomeCode, safeMessage);
        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var current = await LockClaimAsync(connection, transaction, expected, now, cancellationToken)
            .ConfigureAwait(false);
        if (current.IsFailure)
        {
            return Result.Failure(current.Error!);
        }

        await using var update = new NpgsqlCommand(
            $"""
            UPDATE {ConfigurationMigrations.Schema}.distribution_job
            SET completed_at = @now,
                outcome_code = @outcome_code,
                outcome_message = @outcome_message
            WHERE job_id = @job_id;
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("now", now);
        update.Parameters.AddWithValue("outcome_code", outcomeCode);
        update.Parameters.AddWithValue("outcome_message", safeMessage);
        update.Parameters.AddWithValue("job_id", expected.JobId.Value);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await InsertEventAsync(
            connection,
            transaction,
            current.Value,
            expected.ClaimedBy,
            "workload.reject",
            outcomeCode,
            safeMessage,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<ConfigurationWorkloadActivation>> ReadActivatedAsync(
        FacilityScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT revision_id, runtime_generation, alarm_definition_epoch, acknowledged_at
            FROM {ConfigurationMigrations.Schema}.distribution_job
            WHERE scope_id = @scope_id
              AND acknowledged_at IS NOT NULL
              AND outcome_code = 'activated'
            ORDER BY acknowledged_at DESC, job_id DESC
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Failure<ConfigurationWorkloadActivation>(
                "configuration.workload_activation_not_found",
                "No acknowledged workload activation exists.");
        }

        var revisionId = ConfigurationRevisionId.From(reader.GetGuid(0));
        var generation = reader.GetInt64(1);
        var epoch = RevisionNumber.From(checked((ulong)reader.GetInt64(2)));
        var activatedAt = reader.GetFieldValue<DateTimeOffset>(3);
        await reader.CloseAsync().ConfigureAwait(false);
        var revision = await ReadRevisionAsync(
            connection,
            transaction,
            revisionId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new ConfigurationWorkloadActivation(
            revision!,
            generation,
            epoch,
            activatedAt));
    }

    public async Task<Result<ConfigurationWorkloadOutcome>> ReadOutcomeAsync(
        ConfigurationRevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT job_id, scope_id, revision_id, attempts, prepared_at, switched_at,
                   completed_at, outcome_code, outcome_message, runtime_generation,
                   alarm_definition_epoch
            FROM {ConfigurationMigrations.Schema}.distribution_job
            WHERE revision_id = @revision_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision_id", revisionId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Failure<ConfigurationWorkloadOutcome>(
                "configuration.workload_outcome_not_found",
                "The workload deployment outcome was not found.");
        }

        var outcome = new ConfigurationWorkloadOutcome(
            DistributionJobId.From(reader.GetGuid(0)),
            FacilityScopeId.From(reader.GetGuid(1)),
            ConfigurationRevisionId.From(reader.GetGuid(2)),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.IsDBNull(10)
                ? null
                : RevisionNumber.From(checked((ulong)reader.GetInt64(10))));
        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(outcome);
    }

    private async Task<Result<ConfigurationWorkloadClaim>> RecordSwitchCoreAsync(
        ConfigurationWorkloadClaim expected,
        long runtimeGeneration,
        RevisionNumber alarmDefinitionEpoch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var current = await LockClaimAsync(connection, transaction, expected, now, cancellationToken)
            .ConfigureAwait(false);
        if (current.IsFailure)
        {
            return current;
        }

        if (current.Value.PreparedAt is null)
        {
            return Failure<ConfigurationWorkloadClaim>(
                "configuration.workload_not_prepared",
                "Only a prepared whole-scope deployment can be switched.");
        }

        if (current.Value.SwitchedAt is not null &&
            (current.Value.RuntimeGeneration != runtimeGeneration ||
             current.Value.AlarmDefinitionEpoch != alarmDefinitionEpoch))
        {
            return Failure<ConfigurationWorkloadClaim>(
                "configuration.workload_switch_conflict",
                "The deployment was already switched with another runtime generation.");
        }

        if (current.Value.SwitchedAt is null)
        {
            await using var update = new NpgsqlCommand(
                $"""
                UPDATE {ConfigurationMigrations.Schema}.distribution_job
                SET switched_at = @now,
                    runtime_generation = @runtime_generation,
                    alarm_definition_epoch = @alarm_definition_epoch
                WHERE job_id = @job_id;
                """,
                connection,
                transaction);
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("runtime_generation", runtimeGeneration);
            update.Parameters.AddWithValue(
                "alarm_definition_epoch",
                checked((long)alarmDefinitionEpoch.Value));
            update.Parameters.AddWithValue("job_id", expected.JobId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            current = Result.Success((await ReadClaimAsync(
                connection,
                transaction,
                expected.JobId,
                cancellationToken).ConfigureAwait(false))!);
            await InsertEventAsync(
                connection,
                transaction,
                current.Value,
                expected.ClaimedBy,
                "workload.switch",
                null,
                null,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return current;
    }

    private static async Task<Result<ConfigurationWorkloadClaim>> LockClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ConfigurationWorkloadClaim expected,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var current = await ReadClaimAsync(
            connection,
            transaction,
            expected.JobId,
            cancellationToken,
            forUpdate: true).ConfigureAwait(false);
        if (current is null ||
            current.ClaimedBy != expected.ClaimedBy ||
            current.LeaseToken != expected.LeaseToken ||
            current.LeaseUntil <= now ||
            current.Revision.RevisionId != expected.Revision.RevisionId)
        {
            return InvalidLease<ConfigurationWorkloadClaim>();
        }

        await using var currentRevision = new NpgsqlCommand(
            $"""
            SELECT published_revision_id = @revision_id
            FROM {ConfigurationMigrations.Schema}.scope_state
            WHERE scope_id = @scope_id;
            """,
            connection,
            transaction);
        currentRevision.Parameters.AddWithValue("revision_id", expected.Revision.RevisionId.Value);
        currentRevision.Parameters.AddWithValue("scope_id", expected.Revision.ScopeId.Value);
        if (await currentRevision.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            return Failure<ConfigurationWorkloadClaim>(
                "configuration.workload_superseded",
                "The workload deployment was superseded by another published revision.");
        }

        return Result.Success(current);
    }

    private static async Task<ConfigurationWorkloadClaim?> ReadClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DistributionJobId jobId,
        CancellationToken cancellationToken,
        bool forUpdate = false)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT job.job_id, job.lease_token, job.claimed_by, job.lease_until, job.attempts,
                   job.revision_fingerprint, job.prepared_at, job.switched_at,
                   job.runtime_generation, job.alarm_definition_epoch,
                   revision.revision_id, revision.scope_id, revision.revision_number,
                   revision.source_revision_id, revision.manifest::text,
                   revision.manifest_fingerprint, revision.dependencies::text,
                   revision.dependency_fingerprint, revision.version, revision.saved_at,
                   revision.validated_at, revision.published_at, revision.distributed_at,
                   revision.activated_at
            FROM {ConfigurationMigrations.Schema}.distribution_job job
            JOIN {ConfigurationMigrations.Schema}.revision revision
              ON revision.revision_id = job.revision_id
            WHERE job.job_id = @job_id
            {(forUpdate ? "FOR UPDATE OF job" : string.Empty)};
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("job_id", jobId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.IsDBNull(1) ||
            reader.IsDBNull(2) ||
            reader.IsDBNull(3))
        {
            return null;
        }

        var dependencies =
            JsonSerializer.Deserialize<ConfigurationDependency[]>(reader.GetString(16)) ?? [];
        var revision = new ConfigurationRevisionSnapshot(
            ConfigurationRevisionId.From(reader.GetGuid(10)),
            FacilityScopeId.From(reader.GetGuid(11)),
            RevisionNumber.From(checked((ulong)reader.GetInt64(12))),
            reader.IsDBNull(13) ? null : ConfigurationRevisionId.From(reader.GetGuid(13)),
            reader.GetString(14),
            reader.GetString(15),
            dependencies,
            reader.GetString(17),
            reader.GetInt64(18),
            reader.GetFieldValue<DateTimeOffset>(19),
            reader.IsDBNull(20) ? null : reader.GetFieldValue<DateTimeOffset>(20),
            reader.IsDBNull(21) ? null : reader.GetFieldValue<DateTimeOffset>(21),
            reader.IsDBNull(22) ? null : reader.GetFieldValue<DateTimeOffset>(22),
            reader.IsDBNull(23) ? null : reader.GetFieldValue<DateTimeOffset>(23));
        return new ConfigurationWorkloadClaim(
            DistributionJobId.From(reader.GetGuid(0)),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetInt32(4),
            revision,
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9)
                ? null
                : RevisionNumber.From(checked((ulong)reader.GetInt64(9))));
    }

    private static async Task<ConfigurationRevisionSnapshot?> ReadRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ConfigurationRevisionId revisionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT revision_id, scope_id, revision_number, source_revision_id,
                   manifest::text, manifest_fingerprint, dependencies::text,
                   dependency_fingerprint, version, saved_at, validated_at,
                   published_at, distributed_at, activated_at
            FROM {ConfigurationMigrations.Schema}.revision
            WHERE revision_id = @revision_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision_id", revisionId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ConfigurationRevisionSnapshot(
            ConfigurationRevisionId.From(reader.GetGuid(0)),
            FacilityScopeId.From(reader.GetGuid(1)),
            RevisionNumber.From(checked((ulong)reader.GetInt64(2))),
            reader.IsDBNull(3) ? null : ConfigurationRevisionId.From(reader.GetGuid(3)),
            reader.GetString(4),
            reader.GetString(5),
            JsonSerializer.Deserialize<ConfigurationDependency[]>(reader.GetString(6)) ?? [],
            reader.GetString(7),
            reader.GetInt64(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
            reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13));
    }

    private async Task InsertEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ConfigurationWorkloadClaim claim,
        string workerId,
        string action,
        string? outcomeCode,
        string? outcomeMessage,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {ConfigurationMigrations.Schema}.workload_deployment_event
                (event_id, job_id, scope_id, revision_id, worker_id, action,
                 outcome_code, outcome_message, runtime_generation,
                 alarm_definition_epoch, changed_at)
            VALUES
                (@event_id, @job_id, @scope_id, @revision_id, @worker_id, @action,
                 @outcome_code, @outcome_message, @runtime_generation,
                 @alarm_definition_epoch, @changed_at);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("event_id", Guid.NewGuid());
        command.Parameters.AddWithValue("job_id", claim.JobId.Value);
        command.Parameters.AddWithValue("scope_id", claim.Revision.ScopeId.Value);
        command.Parameters.AddWithValue("revision_id", claim.Revision.RevisionId.Value);
        command.Parameters.AddWithValue("worker_id", workerId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("outcome_code", (object?)outcomeCode ?? DBNull.Value);
        command.Parameters.AddWithValue("outcome_message", (object?)outcomeMessage ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "runtime_generation",
            (object?)claim.RuntimeGeneration ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "alarm_definition_epoch",
            claim.AlarmDefinitionEpoch is { } epoch
                ? checked((long)epoch.Value)
                : DBNull.Value);
        command.Parameters.AddWithValue("changed_at", clock.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static void ValidateIdentity(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        if (identity.Length > 128 || identity.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.' and not ':'))
        {
            throw new ArgumentException("A workload identity must be bounded.", nameof(identity));
        }
    }

    private static void ValidateOutcome(string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (code.Length > 128 || message.Length > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(code), "Sanitised deployment outcomes are bounded.");
        }
    }

    private static Result<TValue> InvalidLease<TValue>() =>
        Failure<TValue>(
            "configuration.workload_lease_invalid",
            "The workload deployment lease is stale or belongs to another worker.");

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
