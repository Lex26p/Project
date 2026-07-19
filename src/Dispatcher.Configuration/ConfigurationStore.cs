using System.Text.Json;
using System.Text.RegularExpressions;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;
using NpgsqlTypes;

namespace Dispatcher.Configuration;

public sealed partial class ConfigurationStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;

    public ConfigurationStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock)
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

    public async Task<Result<ConfigurationScopeSnapshot>> ReadScopeAsync(
        FacilityScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var state = await ReadStateAsync(connection, transaction, scopeId, false, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return Failure<ConfigurationScopeSnapshot>("configuration.scope_not_found", "Configuration scope was not found.");
        }

        var revisions = new List<ConfigurationRevisionSnapshot>();
        await using (var command = new NpgsqlCommand(
                         $"""
                         SELECT revision_id, scope_id, revision_number, source_revision_id,
                                manifest::text, manifest_fingerprint, dependencies::text, dependency_fingerprint,
                                version, saved_at, validated_at, published_at, distributed_at, activated_at
                         FROM {ConfigurationMigrations.Schema}.revision
                         WHERE scope_id = @scope_id
                         ORDER BY revision_number;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                revisions.Add(MapRevision(reader));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new ConfigurationScopeSnapshot(
            scopeId,
            state.DraftRevisionId,
            state.PublishedRevisionId,
            state.DistributedRevisionId,
            state.ActivatedRevisionId,
            state.Version,
            revisions));
    }

    public async Task<Result<ConfigurationRevisionSnapshot>> ReadDesiredReleaseAsync(
        FacilityScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var state = await ReadStateAsync(connection, transaction, scopeId, false, cancellationToken).ConfigureAwait(false);
        if (state?.PublishedRevisionId is null)
        {
            return Failure<ConfigurationRevisionSnapshot>(
                "configuration.release_not_found",
                "No published configuration release is available.");
        }

        var revision = await ReadRevisionAsync(
            connection,
            transaction,
            state.PublishedRevisionId.Value,
            false,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(revision!);
    }

    public async Task<Result<ConfigurationRevisionSnapshot>> SaveAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        SaveConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        (string Json, string Fingerprint) manifest;
        (string Json, string Fingerprint, ConfigurationDependency[] Items) dependencies;
        try
        {
            manifest = ConfigurationFingerprint.NormalizeManifest(request.ManifestJson);
            dependencies = ConfigurationFingerprint.NormalizeDependencies(request.Dependencies);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            return Failure<ConfigurationRevisionSnapshot>("configuration.manifest_invalid", exception.Message);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        await EnsureStateAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        var state = (await ReadStateAsync(connection, transaction, scopeId, true, cancellationToken).ConfigureAwait(false))!;
        var now = clock.GetUtcNow();

        ConfigurationRevisionSnapshot revision;
        if (state.DraftRevisionId is not null)
        {
            var current = (await ReadRevisionAsync(
                connection,
                transaction,
                state.DraftRevisionId.Value,
                true,
                cancellationToken).ConfigureAwait(false))!;
            if (request.ExpectedVersion is null || request.ExpectedVersion.Value != current.Version)
            {
                return Failure<ConfigurationRevisionSnapshot>(
                    "configuration.version_conflict",
                    "Draft version changed concurrently.");
            }

            var nextVersion = checked(current.Version + 1);
            await using var update = new NpgsqlCommand(
                $"""
                UPDATE {ConfigurationMigrations.Schema}.revision
                SET manifest = @manifest,
                    manifest_fingerprint = @manifest_fingerprint,
                    dependencies = @dependencies,
                    dependency_fingerprint = @dependency_fingerprint,
                    validated_manifest_fingerprint = NULL,
                    validated_dependency_fingerprint = NULL,
                    validated_at = NULL,
                    saved_at = @saved_at,
                    version = @version
                WHERE revision_id = @revision_id;
                """,
                connection,
                transaction);
            AddContentParameters(update, manifest, dependencies, now);
            update.Parameters.AddWithValue("version", nextVersion);
            update.Parameters.AddWithValue("revision_id", current.RevisionId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            revision = current with
            {
                ManifestJson = manifest.Json,
                ManifestFingerprint = manifest.Fingerprint,
                Dependencies = dependencies.Items,
                DependencyFingerprint = dependencies.Fingerprint,
                Version = nextVersion,
                SavedAt = now,
                ValidatedAt = null,
            };
        }
        else
        {
            if (request.ExpectedVersion is not null)
            {
                return Failure<ConfigurationRevisionSnapshot>(
                    "configuration.version_conflict",
                    "There is no draft with the expected version.");
            }

            var revisionId = ConfigurationRevisionId.New();
            var revisionNumber = RevisionNumber.From(checked((ulong)state.NextRevisionNumber));
            await using (var insert = new NpgsqlCommand(
                             $"""
                             INSERT INTO {ConfigurationMigrations.Schema}.revision
                                 (revision_id, scope_id, revision_number, source_revision_id,
                                  manifest, manifest_fingerprint, dependencies, dependency_fingerprint,
                                  version, saved_at)
                             VALUES
                                 (@revision_id, @scope_id, @revision_number, NULL,
                                  @manifest, @manifest_fingerprint, @dependencies, @dependency_fingerprint,
                                  1, @saved_at);
                             """,
                             connection,
                             transaction))
            {
                insert.Parameters.AddWithValue("revision_id", revisionId.Value);
                insert.Parameters.AddWithValue("scope_id", scopeId.Value);
                insert.Parameters.AddWithValue("revision_number", checked((long)revisionNumber.Value));
                AddContentParameters(insert, manifest, dependencies, now);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await UpdateDraftStateAsync(
                connection,
                transaction,
                scopeId,
                revisionId,
                state.NextRevisionNumber + 1,
                state.Version + 1,
                cancellationToken).ConfigureAwait(false);
            revision = new ConfigurationRevisionSnapshot(
                revisionId,
                scopeId,
                revisionNumber,
                null,
                manifest.Json,
                manifest.Fingerprint,
                dependencies.Items,
                dependencies.Fingerprint,
                1,
                now,
                null,
                null,
                null,
                null);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            authorization,
            scopeId,
            revision.RevisionId,
            null,
            "configuration.save",
            revision.Version,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(revision);
    }

    public Task<Result<ConfigurationRevisionSnapshot>> ValidateAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        ConfigurationRevisionId revisionId,
        long expectedVersion,
        CancellationToken cancellationToken = default) =>
        UpdateDraftAsync(
            authorization,
            scopeId,
            revisionId,
            expectedVersion,
            "configuration.validate",
            async (connection, transaction, revision, nextVersion, now) =>
            {
                await using var command = new NpgsqlCommand(
                    $"""
                    UPDATE {ConfigurationMigrations.Schema}.revision
                    SET validated_manifest_fingerprint = manifest_fingerprint,
                        validated_dependency_fingerprint = dependency_fingerprint,
                        validated_at = @now,
                        version = @version
                    WHERE revision_id = @revision_id;
                    """,
                    connection,
                    transaction);
                command.Parameters.AddWithValue("now", now);
                command.Parameters.AddWithValue("version", nextVersion);
                command.Parameters.AddWithValue("revision_id", revisionId.Value);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return revision with { ValidatedAt = now, Version = nextVersion };
            },
            cancellationToken);

    public async Task<Result<ConfigurationRevisionSnapshot>> PublishAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        PublishConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        var currentDependencies = ConfigurationFingerprint.NormalizeDependencies(request.CurrentDependencies);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        var state = await ReadStateAsync(connection, transaction, scopeId, true, cancellationToken).ConfigureAwait(false);
        var revision = await ReadRevisionAsync(
            connection,
            transaction,
            request.RevisionId,
            true,
            cancellationToken).ConfigureAwait(false);
        if (state?.DraftRevisionId != request.RevisionId || revision is null || revision.ScopeId != scopeId)
        {
            return Failure<ConfigurationRevisionSnapshot>("configuration.draft_not_found", "Draft revision was not found.");
        }

        if (revision.Version != request.ExpectedVersion)
        {
            return Failure<ConfigurationRevisionSnapshot>("configuration.version_conflict", "Draft version changed concurrently.");
        }

        if (revision.ValidatedAt is null ||
            revision.DependencyFingerprint != currentDependencies.Fingerprint ||
            !await ValidationMatchesAsync(connection, transaction, request.RevisionId, cancellationToken).ConfigureAwait(false))
        {
            return Failure<ConfigurationRevisionSnapshot>(
                "configuration.validation_stale",
                "The current manifest and dependencies must be validated before publication.");
        }

        var now = clock.GetUtcNow();
        var nextVersion = checked(revision.Version + 1);
        var jobId = DistributionJobId.New();
        var releaseFingerprint = ConfigurationFingerprint.Combine(
            revision.ManifestFingerprint,
            revision.DependencyFingerprint);
        await using (var update = new NpgsqlCommand(
                         $"""
                         UPDATE {ConfigurationMigrations.Schema}.revision
                         SET published_at = @now, version = @version
                         WHERE revision_id = @revision_id;
                         UPDATE {ConfigurationMigrations.Schema}.scope_state
                         SET draft_revision_id = NULL,
                             published_revision_id = @revision_id,
                             distributed_revision_id = NULL,
                             version = version + 1
                         WHERE scope_id = @scope_id;
                         INSERT INTO {ConfigurationMigrations.Schema}.distribution_job
                             (job_id, scope_id, revision_id, revision_fingerprint, available_at)
                         VALUES (@job_id, @scope_id, @revision_id, @fingerprint, @now);
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("version", nextVersion);
            update.Parameters.AddWithValue("revision_id", request.RevisionId.Value);
            update.Parameters.AddWithValue("scope_id", scopeId.Value);
            update.Parameters.AddWithValue("job_id", jobId.Value);
            update.Parameters.AddWithValue("fingerprint", releaseFingerprint);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            authorization,
            scopeId,
            request.RevisionId,
            jobId,
            "configuration.publish",
            nextVersion,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(revision with { PublishedAt = now, Version = nextVersion });
    }

    public async Task<Result<ConfigurationRevisionSnapshot>> RollbackAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        ConfigurationRevisionId sourceRevisionId,
        long expectedScopeVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        var state = await ReadStateAsync(connection, transaction, scopeId, true, cancellationToken).ConfigureAwait(false);
        var source = await ReadRevisionAsync(
            connection,
            transaction,
            sourceRevisionId,
            false,
            cancellationToken).ConfigureAwait(false);
        if (state is null || state.Version != expectedScopeVersion)
        {
            return Failure<ConfigurationRevisionSnapshot>("configuration.version_conflict", "Scope version changed concurrently.");
        }

        if (state.DraftRevisionId is not null)
        {
            return Failure<ConfigurationRevisionSnapshot>("configuration.draft_exists", "A draft already exists in this scope.");
        }

        if (source is null || source.ScopeId != scopeId || source.PublishedAt is null)
        {
            return Failure<ConfigurationRevisionSnapshot>(
                "configuration.rollback_source_invalid",
                "Rollback source must be a published revision in this scope.");
        }

        var now = clock.GetUtcNow();
        var revisionId = ConfigurationRevisionId.New();
        var number = RevisionNumber.From(checked((ulong)state.NextRevisionNumber));
        await using (var insert = new NpgsqlCommand(
                         $"""
                         INSERT INTO {ConfigurationMigrations.Schema}.revision
                             (revision_id, scope_id, revision_number, source_revision_id,
                              manifest, manifest_fingerprint, dependencies, dependency_fingerprint,
                              version, saved_at)
                         SELECT @revision_id, scope_id, @revision_number, revision_id,
                                manifest, manifest_fingerprint, dependencies, dependency_fingerprint,
                                1, @now
                         FROM {ConfigurationMigrations.Schema}.revision
                         WHERE revision_id = @source_revision_id;
                         """,
                         connection,
                         transaction))
        {
            insert.Parameters.AddWithValue("revision_id", revisionId.Value);
            insert.Parameters.AddWithValue("revision_number", checked((long)number.Value));
            insert.Parameters.AddWithValue("now", now);
            insert.Parameters.AddWithValue("source_revision_id", sourceRevisionId.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await UpdateDraftStateAsync(
            connection,
            transaction,
            scopeId,
            revisionId,
            state.NextRevisionNumber + 1,
            state.Version + 1,
            cancellationToken).ConfigureAwait(false);
        var revision = source with
        {
            RevisionId = revisionId,
            RevisionNumber = number,
            SourceRevisionId = sourceRevisionId,
            Version = 1,
            SavedAt = now,
            ValidatedAt = null,
            PublishedAt = null,
            DistributedAt = null,
            ActivatedAt = null,
        };
        await InsertAuditAsync(
            connection,
            transaction,
            authorization,
            scopeId,
            revisionId,
            null,
            "configuration.rollback_draft",
            1,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(revision);
    }

    public async Task<Result<DistributionJobSnapshot>> ClaimDistributionAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateWorker(workerId, leaseDuration);
        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            WITH candidate AS (
                SELECT job_id
                FROM {ConfigurationMigrations.Schema}.distribution_job
                WHERE scope_id = @scope_id AND completed_at IS NULL
                  AND available_at <= @now AND (lease_until IS NULL OR lease_until <= @now)
                ORDER BY available_at, job_id
                FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE {ConfigurationMigrations.Schema}.distribution_job job
            SET claimed_by = @worker_id, lease_until = @lease_until, attempts = attempts + 1
            FROM candidate WHERE job.job_id = candidate.job_id
            RETURNING job.job_id, job.scope_id, job.revision_id, job.revision_fingerprint,
                      job.claimed_by, job.lease_until, job.attempts, job.completed_at;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("worker_id", workerId);
        command.Parameters.AddWithValue("lease_until", now.Add(leaseDuration));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Failure<DistributionJobSnapshot>("configuration.job_not_available", "No distribution job is available.");
        }

        var job = MapJob(reader);
        await reader.CloseAsync().ConfigureAwait(false);
        var revision = (await ReadRevisionAsync(
            connection,
            transaction,
            job.RevisionId,
            false,
            cancellationToken).ConfigureAwait(false))!;
        await InsertAuditAsync(
            connection,
            transaction,
            authorization,
            scopeId,
            job.RevisionId,
            job.JobId,
            "configuration.distribution_claim",
            revision.Version,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(job);
    }

    public async Task<Result<ConfigurationRevisionSnapshot>> CompleteDistributionAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        DistributionJobId jobId,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorker(workerId, TimeSpan.FromTicks(1));
        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        var job = await ReadJobAsync(connection, transaction, jobId, true, cancellationToken).ConfigureAwait(false);
        if (job is null || job.ScopeId != scopeId || job.CompletedAt is not null ||
            job.ClaimedBy != workerId || job.LeaseUntil <= now)
        {
            return Failure<ConfigurationRevisionSnapshot>(
                "configuration.job_lease_invalid",
                "Distribution job is not held by this worker.");
        }

        var revision = (await ReadRevisionAsync(
            connection,
            transaction,
            job.RevisionId,
            true,
            cancellationToken).ConfigureAwait(false))!;
        var nextVersion = checked(revision.Version + 1);
        await using (var update = new NpgsqlCommand(
                         $"""
                         UPDATE {ConfigurationMigrations.Schema}.distribution_job
                         SET completed_at = @now WHERE job_id = @job_id;
                         UPDATE {ConfigurationMigrations.Schema}.revision
                         SET distributed_at = @now, version = @version WHERE revision_id = @revision_id;
                         UPDATE {ConfigurationMigrations.Schema}.scope_state
                         SET distributed_revision_id = @revision_id, version = version + 1
                         WHERE scope_id = @scope_id AND published_revision_id = @revision_id;
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("job_id", jobId.Value);
            update.Parameters.AddWithValue("version", nextVersion);
            update.Parameters.AddWithValue("revision_id", revision.RevisionId.Value);
            update.Parameters.AddWithValue("scope_id", scopeId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            authorization,
            scopeId,
            revision.RevisionId,
            jobId,
            "configuration.distribute",
            nextVersion,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(revision with { DistributedAt = now, Version = nextVersion });
    }

    public async Task<Result<ConfigurationRevisionSnapshot>> AcknowledgeActivationAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        ConfigurationRevisionId revisionId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        var state = await ReadStateAsync(connection, transaction, scopeId, true, cancellationToken).ConfigureAwait(false);
        var revision = await ReadRevisionAsync(connection, transaction, revisionId, true, cancellationToken).ConfigureAwait(false);
        if (state?.ActivatedRevisionId == revisionId && revision?.ActivatedAt is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(revision);
        }

        if (state?.PublishedRevisionId != revisionId || state.DistributedRevisionId != revisionId ||
            revision is null || revision.Version != expectedVersion)
        {
            return Failure<ConfigurationRevisionSnapshot>(
                "configuration.activation_not_ready",
                "Only the current distributed release can acknowledge activation.");
        }

        var nextVersion = checked(revision.Version + 1);
        await using (var update = new NpgsqlCommand(
                         $"""
                         UPDATE {ConfigurationMigrations.Schema}.revision
                         SET activated_at = @now, version = @version WHERE revision_id = @revision_id;
                         UPDATE {ConfigurationMigrations.Schema}.scope_state
                         SET activated_revision_id = @revision_id, version = version + 1 WHERE scope_id = @scope_id;
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("version", nextVersion);
            update.Parameters.AddWithValue("revision_id", revisionId.Value);
            update.Parameters.AddWithValue("scope_id", scopeId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            authorization,
            scopeId,
            revisionId,
            null,
            "configuration.activate_acknowledge",
            nextVersion,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(revision with { ActivatedAt = now, Version = nextVersion });
    }

    private async Task<Result<ConfigurationRevisionSnapshot>> UpdateDraftAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        ConfigurationRevisionId revisionId,
        long expectedVersion,
        string action,
        Func<NpgsqlConnection, NpgsqlTransaction, ConfigurationRevisionSnapshot, long, DateTimeOffset,
            Task<ConfigurationRevisionSnapshot>> update,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockScopeAsync(connection, transaction, scopeId, cancellationToken).ConfigureAwait(false);
        var state = await ReadStateAsync(connection, transaction, scopeId, true, cancellationToken).ConfigureAwait(false);
        var revision = await ReadRevisionAsync(connection, transaction, revisionId, true, cancellationToken).ConfigureAwait(false);
        if (state?.DraftRevisionId != revisionId || revision is null || revision.ScopeId != scopeId)
        {
            return Failure<ConfigurationRevisionSnapshot>("configuration.draft_not_found", "Draft revision was not found.");
        }

        if (revision.Version != expectedVersion)
        {
            return Failure<ConfigurationRevisionSnapshot>("configuration.version_conflict", "Draft version changed concurrently.");
        }

        var nextVersion = checked(revision.Version + 1);
        var result = await update(connection, transaction, revision, nextVersion, clock.GetUtcNow()).ConfigureAwait(false);
        await InsertAuditAsync(
            connection,
            transaction,
            authorization,
            scopeId,
            revisionId,
            null,
            action,
            nextVersion,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(result);
    }

    private static async Task<bool> ValidationMatchesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ConfigurationRevisionId revisionId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT validated_manifest_fingerprint = manifest_fingerprint
               AND validated_dependency_fingerprint = dependency_fingerprint
            FROM {ConfigurationMigrations.Schema}.revision WHERE revision_id = @revision_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision_id", revisionId.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static void AddContentParameters(
        NpgsqlCommand command,
        (string Json, string Fingerprint) manifest,
        (string Json, string Fingerprint, ConfigurationDependency[] Items) dependencies,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue("manifest", NpgsqlDbType.Jsonb, manifest.Json);
        command.Parameters.AddWithValue("manifest_fingerprint", manifest.Fingerprint);
        command.Parameters.AddWithValue("dependencies", NpgsqlDbType.Jsonb, dependencies.Json);
        command.Parameters.AddWithValue("dependency_fingerprint", dependencies.Fingerprint);
        command.Parameters.AddWithValue("saved_at", now);
    }

    private static async Task EnsureStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FacilityScopeId scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {ConfigurationMigrations.Schema}.scope_state
                (scope_id, next_revision_number, version)
            VALUES (@scope_id, 1, 1) ON CONFLICT DO NOTHING;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateDraftStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FacilityScopeId scopeId,
        ConfigurationRevisionId revisionId,
        long nextRevisionNumber,
        long version,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {ConfigurationMigrations.Schema}.scope_state
            SET draft_revision_id = @revision_id,
                next_revision_number = @next_revision_number,
                version = @version
            WHERE scope_id = @scope_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision_id", revisionId.Value);
        command.Parameters.AddWithValue("next_revision_number", nextRevisionNumber);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ScopeState?> ReadStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FacilityScopeId scopeId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT next_revision_number, draft_revision_id, published_revision_id,
                   distributed_revision_id, activated_revision_id, version
            FROM {ConfigurationMigrations.Schema}.scope_state
            WHERE scope_id = @scope_id
            {(forUpdate ? "FOR UPDATE" : string.Empty)};
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ScopeState(
                reader.GetInt64(0),
                NullableRevision(reader, 1),
                NullableRevision(reader, 2),
                NullableRevision(reader, 3),
                NullableRevision(reader, 4),
                reader.GetInt64(5))
            : null;
    }

    private static async Task<ConfigurationRevisionSnapshot?> ReadRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ConfigurationRevisionId revisionId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT revision_id, scope_id, revision_number, source_revision_id,
                   manifest::text, manifest_fingerprint, dependencies::text, dependency_fingerprint,
                   version, saved_at, validated_at, published_at, distributed_at, activated_at
            FROM {ConfigurationMigrations.Schema}.revision
            WHERE revision_id = @revision_id
            {(forUpdate ? "FOR UPDATE" : string.Empty)};
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision_id", revisionId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapRevision(reader) : null;
    }

    private static async Task<DistributionJobSnapshot?> ReadJobAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DistributionJobId jobId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT job_id, scope_id, revision_id, revision_fingerprint,
                   claimed_by, lease_until, attempts, completed_at
            FROM {ConfigurationMigrations.Schema}.distribution_job
            WHERE job_id = @job_id
            {(forUpdate ? "FOR UPDATE" : string.Empty)};
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("job_id", jobId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapJob(reader) : null;
    }

    private static ConfigurationRevisionSnapshot MapRevision(NpgsqlDataReader reader) => new(
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
        NullableTimestamp(reader, 10),
        NullableTimestamp(reader, 11),
        NullableTimestamp(reader, 12),
        NullableTimestamp(reader, 13));

    private static DistributionJobSnapshot MapJob(NpgsqlDataReader reader) => new(
        DistributionJobId.From(reader.GetGuid(0)),
        FacilityScopeId.From(reader.GetGuid(1)),
        ConfigurationRevisionId.From(reader.GetGuid(2)),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        NullableTimestamp(reader, 5),
        reader.GetInt32(6),
        NullableTimestamp(reader, 7));

    private async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        ConfigurationRevisionId revisionId,
        DistributionJobId? jobId,
        string action,
        long version,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {ConfigurationMigrations.Schema}.mutation_audit
                (audit_id, scope_id, revision_id, job_id, session_id, subject_id,
                 permission, action, resulting_version, changed_at)
            VALUES
                (@audit_id, @scope_id, @revision_id, @job_id, @session_id, @subject_id,
                 @permission, @action, @version, @changed_at);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("audit_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("revision_id", revisionId.Value);
        command.Parameters.AddWithValue("job_id", (object?)jobId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("session_id", authorization.Session.Id.Value);
        command.Parameters.AddWithValue("subject_id", authorization.Session.SubjectId.Value);
        command.Parameters.AddWithValue("permission", authorization.Permission.Value);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("version", version);
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

    private static ConfigurationRevisionId? NullableRevision(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ConfigurationRevisionId.From(reader.GetGuid(ordinal));

    private static DateTimeOffset? NullableTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static void ValidateWorker(string workerId, TimeSpan leaseDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (workerId.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
    }

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));

    private sealed record ScopeState(
        long NextRevisionNumber,
        ConfigurationRevisionId? DraftRevisionId,
        ConfigurationRevisionId? PublishedRevisionId,
        ConfigurationRevisionId? DistributedRevisionId,
        ConfigurationRevisionId? ActivatedRevisionId,
        long Version);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
