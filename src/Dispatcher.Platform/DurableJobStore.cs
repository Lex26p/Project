using System.Text.Json;
using Dispatcher.Semantics;
using Npgsql;
using NpgsqlTypes;

namespace Dispatcher.Platform;

public readonly record struct JobId
{
    private readonly CanonicalId<JobId> value;

    private JobId(CanonicalId<JobId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static JobId New() => new(CanonicalId.New<JobId>());

    public static JobId From(Guid value) => new(CanonicalId.From<JobId>(value));
}

public sealed record DurableJob(
    JobId Id,
    string Kind,
    string Payload,
    DateTimeOffset AvailableAt,
    string ClaimedBy,
    DateTimeOffset LeaseUntil,
    int Attempts);

public sealed class DurableJobStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly PlatformDatabaseRole databaseRole;

    public DurableJobStore(NpgsqlDataSource dataSource, string databaseRole)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        this.dataSource = dataSource;
        this.databaseRole = PlatformDatabaseRole.From(databaseRole);
    }

    public async Task EnqueueAsync(
        JobId jobId,
        string kind,
        string payload,
        DateTimeOffset availableAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        RequireUtc(availableAt, nameof(availableAt));
        using var document = JsonDocument.Parse(payload);
        var normalizedPayload = document.RootElement.GetRawText();

        using var activity = PlatformDiagnostics.ActivitySource.StartActivity("job.enqueue");
        activity?.SetTag("job.kind", kind);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await databaseRole.SetLocalAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {PlatformMigrations.Schema}.durable_job (job_id, kind, payload, available_at)
            VALUES (@job_id, @kind, @payload, @available_at);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("job_id", jobId.Value);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, normalizedPayload);
        command.Parameters.AddWithValue("available_at", availableAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        PlatformDiagnostics.JobsEnqueued.Add(1);
    }

    public async Task<DurableJob?> ClaimNextAsync(
        string workerId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateWorker(workerId);
        RequireUtc(now, nameof(now));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        using var activity = PlatformDiagnostics.ActivitySource.StartActivity("job.claim");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await databaseRole.SetLocalAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            WITH candidate AS (
                SELECT job_id
                FROM {PlatformMigrations.Schema}.durable_job
                WHERE completed_at IS NULL
                  AND available_at <= @now
                  AND (lease_until IS NULL OR lease_until <= @now)
                ORDER BY available_at, job_id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE {PlatformMigrations.Schema}.durable_job AS job
            SET claimed_by = @worker_id,
                lease_until = @lease_until,
                attempts = attempts + 1
            FROM candidate
            WHERE job.job_id = candidate.job_id
            RETURNING job.job_id, job.kind, job.payload::text, job.available_at,
                      job.claimed_by, job.lease_until, job.attempts;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("worker_id", workerId);
        command.Parameters.AddWithValue("lease_until", now.Add(leaseDuration));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        DurableJob? job = null;
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            job = new DurableJob(
                JobId.From(reader.GetGuid(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetInt32(6));
        }

        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (job is not null)
        {
            PlatformDiagnostics.JobsClaimed.Add(1);
        }

        return job;
    }

    public async Task<bool> CompleteAsync(
        JobId jobId,
        string workerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateWorker(workerId);
        RequireUtc(now, nameof(now));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await databaseRole.SetLocalAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {PlatformMigrations.Schema}.durable_job
            SET completed_at = @now
            WHERE job_id = @job_id
              AND claimed_by = @worker_id
              AND lease_until > @now
              AND completed_at IS NULL;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("job_id", jobId.Value);
        command.Parameters.AddWithValue("worker_id", workerId);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static void ValidateWorker(string workerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (workerId.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Job timestamps must use the UTC offset.", parameterName);
        }
    }
}
