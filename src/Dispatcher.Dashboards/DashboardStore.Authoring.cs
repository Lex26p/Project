using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;

namespace Dispatcher.Dashboards;

public sealed partial class DashboardStore
{
    public Task<Result<DashboardAuthoringRevisionSnapshot>> SaveDashboardDraftAsync(
        AuthorizedMutation authorization,
        DashboardId dashboardId,
        SaveDashboardDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SaveDraftAsync(
            authorization,
            dashboardId.Value,
            DashboardAuthoringKind.Dashboard,
            request.ExpectedVersion,
            (revisionId, revisionNumber) =>
            {
                var revision = new DashboardRevision(
                    dashboardId,
                    DashboardRevisionId.From(revisionId),
                    revisionNumber,
                    request.Content.Name,
                    request.Content.Description,
                    request.Content.Windows,
                    request.Content.Dependencies,
                    DateTimeOffset.UnixEpoch);
                var encoded = DashboardManifestCodec.Encode(revision);
                return new NormalizedDraft(
                    encoded.ManifestJson,
                    encoded.ManifestFingerprint,
                    encoded.DependenciesJson,
                    encoded.DependencyFingerprint);
            },
            cancellationToken);
    }

    public Task<Result<DashboardAuthoringRevisionSnapshot>> SaveMimicDraftAsync(
        AuthorizedMutation authorization,
        MimicId mimicId,
        SaveMimicDraftRequest request,
        SvgIntakeLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);
        return SaveDraftAsync(
            authorization,
            mimicId.Value,
            DashboardAuthoringKind.Mimic,
            request.ExpectedVersion,
            (_, _) =>
            {
                var normalized = MimicSvgSanitizer.Normalize(request.Content, limits);
                return new NormalizedDraft(
                    normalized.Json,
                    normalized.ContentFingerprint,
                    normalized.DependenciesJson,
                    normalized.DependencyFingerprint);
            },
            cancellationToken);
    }

    public async Task<Result<DashboardAuthoringRevisionSnapshot>> ValidateAuthoringAsync(
        AuthorizedMutation authorization,
        Guid resourceId,
        DashboardAuthoringKind kind,
        Guid revisionId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockResourceAsync(connection, transaction, resourceId, kind, cancellationToken).ConfigureAwait(false);
        var resource = await ReadResourceAsync(connection, transaction, resourceId, cancellationToken).ConfigureAwait(false);
        var revision = await ReadAuthoringRevisionAsync(
            connection, transaction, revisionId, true, cancellationToken).ConfigureAwait(false);
        if (resource?.DraftRevisionId != revisionId || revision is null || revision.Kind != kind)
        {
            return Failure<DashboardAuthoringRevisionSnapshot>("dashboard.draft_not_found", "Editor draft was not found.");
        }

        if (revision.Version != expectedVersion)
        {
            return Failure<DashboardAuthoringRevisionSnapshot>("dashboard.version_conflict", "Editor draft changed concurrently.");
        }

        var now = clock.GetUtcNow();
        var nextVersion = checked(revision.Version + 1);
        await using (var command = new NpgsqlCommand(
                         $"""
                         UPDATE {DashboardMigrations.Schema}.authoring_revision
                         SET validated_content_fingerprint = content_fingerprint,
                             validated_dependency_fingerprint = dependency_fingerprint,
                             validated_at = @now,
                             version = @version
                         WHERE revision_id = @revision_id;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("version", nextVersion);
            command.Parameters.AddWithValue("revision_id", revisionId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertEditorAuditAsync(
            connection, transaction, authorization, resourceId, kind, revisionId,
            "editor.validate", nextVersion, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(revision with { Version = nextVersion, ValidatedAt = now });
    }

    public async Task<Result<DashboardAuthoringRevisionSnapshot>> PublishAuthoringAsync(
        AuthorizedMutation authorization,
        Guid resourceId,
        DashboardAuthoringKind kind,
        PublishAuthoringRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockResourceAsync(connection, transaction, resourceId, kind, cancellationToken).ConfigureAwait(false);
        var resource = await ReadResourceAsync(connection, transaction, resourceId, cancellationToken).ConfigureAwait(false);
        var stored = await ReadStoredRevisionAsync(
            connection, transaction, request.RevisionId, true, cancellationToken).ConfigureAwait(false);
        if (resource?.DraftRevisionId != request.RevisionId || stored is null || stored.Snapshot.Kind != kind)
        {
            return Failure<DashboardAuthoringRevisionSnapshot>("dashboard.draft_not_found", "Editor draft was not found.");
        }

        if (stored.Snapshot.Version != request.ExpectedVersion)
        {
            return Failure<DashboardAuthoringRevisionSnapshot>("dashboard.version_conflict", "Editor draft changed concurrently.");
        }

        if (stored.Snapshot.ValidatedAt is null ||
            stored.ValidatedContentFingerprint != stored.Snapshot.ContentFingerprint ||
            stored.ValidatedDependencyFingerprint != stored.Snapshot.DependencyFingerprint)
        {
            return Failure<DashboardAuthoringRevisionSnapshot>(
                "dashboard.validation_stale",
                "The exact editor revision must be validated before publication.");
        }

        var now = clock.GetUtcNow();
        if (kind == DashboardAuthoringKind.Dashboard)
        {
            var draft = DashboardManifestCodec.Decode(stored.ContentJson, stored.DependenciesJson);
            var publishedRevision = draft with { PublishedAt = now };
            var encoded = DashboardManifestCodec.Encode(publishedRevision);
            await PublishDashboardInTransactionAsync(
                connection, transaction, publishedRevision, encoded, cancellationToken).ConfigureAwait(false);
        }

        var nextVersion = checked(stored.Snapshot.Version + 1);
        await using (var update = new NpgsqlCommand(
                         $"""
                         UPDATE {DashboardMigrations.Schema}.authoring_revision
                         SET published_at = @now, version = @version
                         WHERE revision_id = @revision_id;
                         UPDATE {DashboardMigrations.Schema}.authoring_resource
                         SET draft_revision_id = NULL,
                             published_revision_id = @revision_id,
                             version = version + 1
                         WHERE resource_id = @resource_id;
                         """,
                         connection,
                         transaction))
        {
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("version", nextVersion);
            update.Parameters.AddWithValue("revision_id", request.RevisionId);
            update.Parameters.AddWithValue("resource_id", resourceId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertEditorAuditAsync(
            connection, transaction, authorization, resourceId, kind, request.RevisionId,
            "editor.publish", nextVersion, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(stored.Snapshot with { Version = nextVersion, PublishedAt = now });
    }

    public async Task<Result<DashboardAuthoringRevisionSnapshot>> RollbackAuthoringAsync(
        AuthorizedMutation authorization,
        Guid resourceId,
        DashboardAuthoringKind kind,
        Guid sourceRevisionId,
        long expectedResourceVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await LockResourceAsync(connection, transaction, resourceId, kind, cancellationToken).ConfigureAwait(false);
        var resource = await ReadResourceAsync(connection, transaction, resourceId, cancellationToken).ConfigureAwait(false);
        var source = await ReadAuthoringRevisionAsync(
            connection, transaction, sourceRevisionId, false, cancellationToken).ConfigureAwait(false);
        if (resource is null || resource.Version != expectedResourceVersion)
        {
            return Failure<DashboardAuthoringRevisionSnapshot>("dashboard.version_conflict", "Editor resource changed concurrently.");
        }

        if (resource.DraftRevisionId is not null)
        {
            return Failure<DashboardAuthoringRevisionSnapshot>("dashboard.draft_exists", "An editor draft already exists.");
        }

        if (source is null || source.ResourceId != resourceId || source.Kind != kind || source.PublishedAt is null)
        {
            return Failure<DashboardAuthoringRevisionSnapshot>(
                "dashboard.rollback_source_invalid",
                "Rollback source must be a published revision of the same resource.");
        }

        var now = clock.GetUtcNow();
        var revisionId = Guid.CreateVersion7();
        var revisionNumber = checked((ulong)resource.NextRevisionNumber);
        await using (var insert = new NpgsqlCommand(
                         $"""
                         INSERT INTO {DashboardMigrations.Schema}.authoring_revision
                             (revision_id, resource_id, kind, revision_number, source_revision_id,
                              content, content_fingerprint, dependencies, dependency_fingerprint,
                              version, saved_at)
                         SELECT @revision_id, resource_id, kind, @revision_number, revision_id,
                                content, content_fingerprint, dependencies, dependency_fingerprint,
                                1, @now
                         FROM {DashboardMigrations.Schema}.authoring_revision
                         WHERE revision_id = @source_revision_id;
                         UPDATE {DashboardMigrations.Schema}.authoring_resource
                         SET draft_revision_id = @revision_id,
                             next_revision_number = next_revision_number + 1,
                             version = version + 1
                         WHERE resource_id = @resource_id;
                         """,
                         connection,
                         transaction))
        {
            insert.Parameters.AddWithValue("revision_id", revisionId);
            insert.Parameters.AddWithValue("revision_number", checked((long)revisionNumber));
            insert.Parameters.AddWithValue("now", now);
            insert.Parameters.AddWithValue("source_revision_id", sourceRevisionId);
            insert.Parameters.AddWithValue("resource_id", resourceId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var snapshot = source with
        {
            RevisionId = revisionId,
            RevisionNumber = revisionNumber,
            SourceRevisionId = sourceRevisionId,
            Version = 1,
            SavedAt = now,
            ValidatedAt = null,
            PublishedAt = null,
        };
        await InsertEditorAuditAsync(
            connection, transaction, authorization, resourceId, kind, revisionId,
            "editor.rollback_draft", 1, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(snapshot);
    }

    public async Task<DashboardAuthoringState?> ReadAuthoringStateAsync(
        Guid resourceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var resource = await ReadResourceAsync(connection, transaction, resourceId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return resource is null
            ? null
            : new DashboardAuthoringState(
                resource.ResourceId, resource.Kind, resource.DraftRevisionId,
                resource.PublishedRevisionId, resource.Version);
    }

    public async Task<DashboardDraftSnapshot?> ReadDashboardDraftAsync(
        DashboardId dashboardId,
        CancellationToken cancellationToken = default)
    {
        var stored = await ReadDraftAsync(
            dashboardId.Value, DashboardAuthoringKind.Dashboard, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        var revision = DashboardManifestCodec.Decode(stored.ContentJson, stored.DependenciesJson);
        return new DashboardDraftSnapshot(
            stored.Snapshot,
            new DashboardDraftContent(
                revision.Name, revision.Description, revision.Windows, revision.Dependencies));
    }

    public async Task<MimicDraftSnapshot?> ReadMimicDraftAsync(
        MimicId mimicId,
        CancellationToken cancellationToken = default)
    {
        var stored = await ReadDraftAsync(
            mimicId.Value, DashboardAuthoringKind.Mimic, cancellationToken).ConfigureAwait(false);
        return stored is null
            ? null
            : new MimicDraftSnapshot(
                stored.Snapshot,
                MimicSvgSanitizer.Decode(stored.ContentJson, stored.DependenciesJson));
    }

    private async Task<StoredRevision?> ReadDraftAsync(
        Guid resourceId,
        DashboardAuthoringKind kind,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var resource = await ReadResourceAsync(connection, transaction, resourceId, cancellationToken).ConfigureAwait(false);
        var stored = resource?.Kind == kind && resource.DraftRevisionId is not null
            ? await ReadStoredRevisionAsync(
                connection, transaction, resource.DraftRevisionId.Value, false, cancellationToken).ConfigureAwait(false)
            : null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    private async Task<Result<DashboardAuthoringRevisionSnapshot>> SaveDraftAsync(
        AuthorizedMutation authorization,
        Guid resourceId,
        DashboardAuthoringKind kind,
        long? expectedVersion,
        Func<Guid, ulong, NormalizedDraft> normalize,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await EnsureResourceAsync(connection, transaction, resourceId, kind, cancellationToken).ConfigureAwait(false);
        await LockResourceAsync(connection, transaction, resourceId, kind, cancellationToken).ConfigureAwait(false);
        var resource = (await ReadResourceAsync(connection, transaction, resourceId, cancellationToken).ConfigureAwait(false))!;
        var now = clock.GetUtcNow();
        DashboardAuthoringRevisionSnapshot snapshot;

        if (resource.DraftRevisionId is null)
        {
            if (expectedVersion is not null)
            {
                return Failure<DashboardAuthoringRevisionSnapshot>("dashboard.version_conflict", "There is no draft with the expected version.");
            }

            var revisionId = Guid.CreateVersion7();
            var revisionNumber = checked((ulong)resource.NextRevisionNumber);
            NormalizedDraft content;
            try
            {
                content = normalize(revisionId, revisionNumber);
            }
            catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
            {
                return Failure<DashboardAuthoringRevisionSnapshot>("dashboard.content_invalid", exception.Message);
            }

            await InsertDraftAsync(
                connection, transaction, resourceId, kind, revisionId, revisionNumber,
                content, now, cancellationToken).ConfigureAwait(false);
            snapshot = new DashboardAuthoringRevisionSnapshot(
                resourceId, kind, revisionId, revisionNumber, null, 1,
                content.ContentFingerprint, content.DependencyFingerprint, now, null, null);
        }
        else
        {
            var current = (await ReadAuthoringRevisionAsync(
                connection, transaction, resource.DraftRevisionId.Value, true, cancellationToken).ConfigureAwait(false))!;
            if (expectedVersion != current.Version)
            {
                return Failure<DashboardAuthoringRevisionSnapshot>("dashboard.version_conflict", "Editor draft changed concurrently.");
            }

            NormalizedDraft content;
            try
            {
                content = normalize(current.RevisionId, current.RevisionNumber);
            }
            catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException)
            {
                return Failure<DashboardAuthoringRevisionSnapshot>("dashboard.content_invalid", exception.Message);
            }

            var nextVersion = checked(current.Version + 1);
            await using var update = new NpgsqlCommand(
                $"""
                UPDATE {DashboardMigrations.Schema}.authoring_revision
                SET content = @content::jsonb,
                    content_fingerprint = @content_fingerprint,
                    dependencies = @dependencies::jsonb,
                    dependency_fingerprint = @dependency_fingerprint,
                    validated_content_fingerprint = NULL,
                    validated_dependency_fingerprint = NULL,
                    validated_at = NULL,
                    version = @version,
                    saved_at = @now
                WHERE revision_id = @revision_id;
                """,
                connection,
                transaction);
            AddDraftParameters(update, content);
            update.Parameters.AddWithValue("version", nextVersion);
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("revision_id", current.RevisionId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            snapshot = current with
            {
                Version = nextVersion,
                ContentFingerprint = content.ContentFingerprint,
                DependencyFingerprint = content.DependencyFingerprint,
                SavedAt = now,
                ValidatedAt = null,
            };
        }

        await InsertEditorAuditAsync(
            connection, transaction, authorization, resourceId, kind, snapshot.RevisionId,
            "editor.save", snapshot.Version, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(snapshot);
    }

    private static async Task EnsureResourceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid resourceId,
        DashboardAuthoringKind kind,
        CancellationToken cancellationToken)
    {
        long nextRevisionNumber = 1;
        Guid? publishedRevisionId = null;
        if (kind == DashboardAuthoringKind.Dashboard)
        {
            await using var read = new NpgsqlCommand(
                $"""
                SELECT COALESCE(MAX(r.revision_number) + 1, 1), d.published_revision_id
                FROM {DashboardMigrations.Schema}.dashboard d
                LEFT JOIN {DashboardMigrations.Schema}.published_revision r
                  ON r.dashboard_id = d.dashboard_id
                WHERE d.dashboard_id = @resource_id
                GROUP BY d.published_revision_id;
                """,
                connection,
                transaction);
            read.Parameters.AddWithValue("resource_id", resourceId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                nextRevisionNumber = reader.GetInt64(0);
                publishedRevisionId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            }

            await reader.CloseAsync().ConfigureAwait(false);
        }

        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {DashboardMigrations.Schema}.authoring_resource
                (resource_id, kind, next_revision_number, draft_revision_id, published_revision_id, version)
            VALUES (@resource_id, @kind, @next_revision_number, NULL, @published_revision_id, 1)
            ON CONFLICT (resource_id) DO NOTHING;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("resource_id", resourceId);
        command.Parameters.AddWithValue("kind", (short)kind);
        command.Parameters.AddWithValue("next_revision_number", nextRevisionNumber);
        command.Parameters.AddWithValue("published_revision_id", (object?)publishedRevisionId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task LockResourceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid resourceId,
        DashboardAuthoringKind kind,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT kind FROM {DashboardMigrations.Schema}.authoring_resource WHERE resource_id = @resource_id FOR UPDATE;",
            connection,
            transaction);
        command.Parameters.AddWithValue("resource_id", resourceId);
        var stored = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (stored is not short storedKind || storedKind != (short)kind)
        {
            throw new InvalidOperationException("Authoring resource kind does not match.");
        }
    }

    private static async Task InsertDraftAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid resourceId,
        DashboardAuthoringKind kind,
        Guid revisionId,
        ulong revisionNumber,
        NormalizedDraft content,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {DashboardMigrations.Schema}.authoring_revision
                (revision_id, resource_id, kind, revision_number, source_revision_id,
                 content, content_fingerprint, dependencies, dependency_fingerprint,
                 version, saved_at)
            VALUES (@revision_id, @resource_id, @kind, @revision_number, NULL,
                    @content::jsonb, @content_fingerprint, @dependencies::jsonb, @dependency_fingerprint,
                    1, @now);
            UPDATE {DashboardMigrations.Schema}.authoring_resource
            SET draft_revision_id = @revision_id,
                next_revision_number = next_revision_number + 1,
                version = version + 1
            WHERE resource_id = @resource_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision_id", revisionId);
        command.Parameters.AddWithValue("resource_id", resourceId);
        command.Parameters.AddWithValue("kind", (short)kind);
        command.Parameters.AddWithValue("revision_number", checked((long)revisionNumber));
        command.Parameters.AddWithValue("now", now);
        AddDraftParameters(command, content);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddDraftParameters(NpgsqlCommand command, NormalizedDraft content)
    {
        command.Parameters.AddWithValue("content", content.ContentJson);
        command.Parameters.AddWithValue("content_fingerprint", content.ContentFingerprint);
        command.Parameters.AddWithValue("dependencies", content.DependenciesJson);
        command.Parameters.AddWithValue("dependency_fingerprint", content.DependencyFingerprint);
    }

    private static async Task PublishDashboardInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DashboardRevision revision,
        (string ManifestJson, string ManifestFingerprint, string DependenciesJson, string DependencyFingerprint) encoded,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {DashboardMigrations.Schema}.dashboard
                (dashboard_id, name, description, published_revision_id, published_revision_number)
            VALUES (@dashboard_id, @name, @description, NULL, NULL)
            ON CONFLICT (dashboard_id) DO NOTHING;
            INSERT INTO {DashboardMigrations.Schema}.published_revision
                (revision_id, dashboard_id, revision_number, manifest, manifest_fingerprint,
                 dependencies, dependency_fingerprint, published_at)
            VALUES (@revision_id, @dashboard_id, @revision_number, @manifest::jsonb, @manifest_fingerprint,
                    @dependencies::jsonb, @dependency_fingerprint, @published_at);
            UPDATE {DashboardMigrations.Schema}.dashboard
            SET name = @name,
                description = @description,
                published_revision_id = @revision_id,
                published_revision_number = @revision_number
            WHERE dashboard_id = @dashboard_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("dashboard_id", revision.DashboardId.Value);
        command.Parameters.AddWithValue("name", revision.Name.Trim());
        command.Parameters.AddWithValue("description", (object?)revision.Description?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("revision_id", revision.RevisionId.Value);
        command.Parameters.AddWithValue("revision_number", checked((long)revision.RevisionNumber));
        command.Parameters.AddWithValue("manifest", encoded.ManifestJson);
        command.Parameters.AddWithValue("manifest_fingerprint", encoded.ManifestFingerprint);
        command.Parameters.AddWithValue("dependencies", encoded.DependenciesJson);
        command.Parameters.AddWithValue("dependency_fingerprint", encoded.DependencyFingerprint);
        command.Parameters.AddWithValue("published_at", revision.PublishedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ResourceRecord?> ReadResourceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT resource_id, kind, next_revision_number, draft_revision_id, published_revision_id, version
            FROM {DashboardMigrations.Schema}.authoring_resource
            WHERE resource_id = @resource_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("resource_id", resourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ResourceRecord(
                reader.GetGuid(0),
                (DashboardAuthoringKind)reader.GetInt16(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.GetInt64(5))
            : null;
    }

    private static async Task<DashboardAuthoringRevisionSnapshot?> ReadAuthoringRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid revisionId,
        bool forUpdate,
        CancellationToken cancellationToken) =>
        (await ReadStoredRevisionAsync(connection, transaction, revisionId, forUpdate, cancellationToken)
            .ConfigureAwait(false))?.Snapshot;

    private static async Task<StoredRevision?> ReadStoredRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid revisionId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT resource_id, kind, revision_id, revision_number, source_revision_id,
                   version, content_fingerprint, dependency_fingerprint,
                   saved_at, validated_at, published_at,
                   content::text, dependencies::text,
                   validated_content_fingerprint, validated_dependency_fingerprint
            FROM {DashboardMigrations.Schema}.authoring_revision
            WHERE revision_id = @revision_id
            {(forUpdate ? "FOR UPDATE" : string.Empty)};
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("revision_id", revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var snapshot = new DashboardAuthoringRevisionSnapshot(
            reader.GetGuid(0),
            (DashboardAuthoringKind)reader.GetInt16(1),
            reader.GetGuid(2),
            checked((ulong)reader.GetInt64(3)),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10));
        return new StoredRevision(
            snapshot,
            reader.GetString(11),
            reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14));
    }

    private async Task InsertEditorAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuthorizedMutation authorization,
        Guid resourceId,
        DashboardAuthoringKind kind,
        Guid revisionId,
        string action,
        long resultingVersion,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {DashboardMigrations.Schema}.editor_audit
                (audit_id, resource_id, kind, revision_id, session_id, subject_id,
                 permission, action, resulting_version, changed_at)
            VALUES (@audit_id, @resource_id, @kind, @revision_id, @session_id, @subject_id,
                    @permission, @action, @resulting_version, @changed_at);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("audit_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("resource_id", resourceId);
        command.Parameters.AddWithValue("kind", (short)kind);
        command.Parameters.AddWithValue("revision_id", revisionId);
        command.Parameters.AddWithValue("session_id", authorization.Session.Id.Value);
        command.Parameters.AddWithValue("subject_id", authorization.Session.SubjectId.Value);
        command.Parameters.AddWithValue("permission", authorization.Permission.Value);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("resulting_version", resultingVersion);
        command.Parameters.AddWithValue("changed_at", clock.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));

    private sealed record NormalizedDraft(
        string ContentJson,
        string ContentFingerprint,
        string DependenciesJson,
        string DependencyFingerprint);
    private sealed record ResourceRecord(
        Guid ResourceId,
        DashboardAuthoringKind Kind,
        long NextRevisionNumber,
        Guid? DraftRevisionId,
        Guid? PublishedRevisionId,
        long Version);
    private sealed record StoredRevision(
        DashboardAuthoringRevisionSnapshot Snapshot,
        string ContentJson,
        string DependenciesJson,
        string? ValidatedContentFingerprint,
        string? ValidatedDependencyFingerprint);
}
