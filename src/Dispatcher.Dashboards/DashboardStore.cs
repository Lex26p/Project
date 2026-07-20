using System.Text.RegularExpressions;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;

namespace Dispatcher.Dashboards;

public sealed partial class DashboardStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;

    public DashboardStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock)
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

    public async Task<PublishedDashboardManifest> PublishAsync(
        DashboardRevision revision,
        CancellationToken cancellationToken = default)
    {
        var encoded = DashboardManifestCodec.Encode(revision);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        await using (var dashboard = connection.CreateCommand())
        {
            dashboard.Transaction = transaction;
            dashboard.CommandText = $"""
                INSERT INTO {DashboardMigrations.Schema}.dashboard
                    (dashboard_id, name, description, published_revision_id, published_revision_number)
                VALUES (@dashboard_id, @name, @description, NULL, NULL)
                ON CONFLICT (dashboard_id) DO NOTHING;
                """;
            dashboard.Parameters.AddWithValue("dashboard_id", revision.DashboardId.Value);
            dashboard.Parameters.AddWithValue("name", revision.Name.Trim());
            dashboard.Parameters.AddWithValue("description", (object?)revision.Description?.Trim() ?? DBNull.Value);
            await dashboard.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = $"""
                INSERT INTO {DashboardMigrations.Schema}.published_revision
                    (revision_id, dashboard_id, revision_number, manifest, manifest_fingerprint,
                     dependencies, dependency_fingerprint, published_at)
                VALUES (@revision_id, @dashboard_id, @revision_number, @manifest::jsonb, @manifest_fingerprint,
                        @dependencies::jsonb, @dependency_fingerprint, @published_at);
                """;
            insert.Parameters.AddWithValue("revision_id", revision.RevisionId.Value);
            insert.Parameters.AddWithValue("dashboard_id", revision.DashboardId.Value);
            insert.Parameters.AddWithValue("revision_number", checked((long)revision.RevisionNumber));
            insert.Parameters.AddWithValue("manifest", encoded.ManifestJson);
            insert.Parameters.AddWithValue("manifest_fingerprint", encoded.ManifestFingerprint);
            insert.Parameters.AddWithValue("dependencies", encoded.DependenciesJson);
            insert.Parameters.AddWithValue("dependency_fingerprint", encoded.DependencyFingerprint);
            insert.Parameters.AddWithValue("published_at", revision.PublishedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var activate = connection.CreateCommand())
        {
            activate.Transaction = transaction;
            activate.CommandText = $"""
                UPDATE {DashboardMigrations.Schema}.dashboard
                SET name = @name,
                    description = @description,
                    published_revision_id = @revision_id,
                    published_revision_number = @revision_number
                WHERE dashboard_id = @dashboard_id
                  AND (published_revision_number IS NULL OR published_revision_number < @revision_number);
                """;
            activate.Parameters.AddWithValue("dashboard_id", revision.DashboardId.Value);
            activate.Parameters.AddWithValue("revision_id", revision.RevisionId.Value);
            activate.Parameters.AddWithValue("revision_number", checked((long)revision.RevisionNumber));
            activate.Parameters.AddWithValue("name", revision.Name.Trim());
            activate.Parameters.AddWithValue("description", (object?)revision.Description?.Trim() ?? DBNull.Value);
            if (await activate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("Dashboard publication must advance the whole active revision.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PublishedDashboardManifest(
            revision, encoded.ManifestJson, encoded.ManifestFingerprint, encoded.DependencyFingerprint);
    }

    public async Task<PublishedDashboardManifest?> ReadPublishedAsync(
        DashboardId dashboardId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT r.manifest::text, r.manifest_fingerprint, r.dependencies::text, r.dependency_fingerprint
            FROM {DashboardMigrations.Schema}.dashboard d
            JOIN {DashboardMigrations.Schema}.published_revision r
              ON r.revision_id = d.published_revision_id
            WHERE d.dashboard_id = @dashboard_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("dashboard_id", dashboardId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.CloseAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var manifestJson = reader.GetString(0);
        var dependenciesJson = reader.GetString(2);
        var result = new PublishedDashboardManifest(
            DashboardManifestCodec.Decode(manifestJson, dependenciesJson),
            manifestJson,
            reader.GetString(1),
            reader.GetString(3));
        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<DashboardCatalogItem>> ReadCatalogAsync(
        SubjectId subjectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT d.dashboard_id, d.name, d.description,
                   COALESCE(p.is_favorite, false), p.last_opened_at
            FROM {DashboardMigrations.Schema}.dashboard d
            LEFT JOIN {DashboardMigrations.Schema}.personal_dashboard p
              ON p.dashboard_id = d.dashboard_id AND p.subject_id = @subject_id
            WHERE d.published_revision_id IS NOT NULL
            ORDER BY COALESCE(p.is_favorite, false) DESC, p.last_opened_at DESC NULLS LAST, lower(d.name), d.dashboard_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("subject_id", subjectId.Value);
        var items = new List<DashboardCatalogItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new DashboardCatalogItem(
                DashboardId.From(reader.GetGuid(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
        }

        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return items;
    }

    public Task SetFavoriteAsync(
        SubjectId subjectId,
        DashboardId dashboardId,
        bool favorite,
        CancellationToken cancellationToken = default) =>
        UpdatePersonalAsync(subjectId, dashboardId, favorite, false, cancellationToken);

    public Task RecordOpenedAsync(
        SubjectId subjectId,
        DashboardId dashboardId,
        CancellationToken cancellationToken = default) =>
        UpdatePersonalAsync(subjectId, dashboardId, false, true, cancellationToken);

    public async Task<DashboardId?> ReadLastDashboardAsync(
        SubjectId subjectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT dashboard_id FROM {DashboardMigrations.Schema}.personal_landing WHERE subject_id = @subject_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("subject_id", subjectId.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return value is Guid dashboardId ? DashboardId.From(dashboardId) : null;
    }

    private async Task UpdatePersonalAsync(
        SubjectId subjectId,
        DashboardId dashboardId,
        bool favorite,
        bool opened,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = opened
                ? $"""
                    INSERT INTO {DashboardMigrations.Schema}.personal_dashboard
                        (subject_id, dashboard_id, is_favorite, last_opened_at)
                    VALUES (@subject_id, @dashboard_id, false, @now)
                    ON CONFLICT (subject_id, dashboard_id) DO UPDATE SET last_opened_at = EXCLUDED.last_opened_at;
                    """
                : $"""
                    INSERT INTO {DashboardMigrations.Schema}.personal_dashboard
                        (subject_id, dashboard_id, is_favorite, last_opened_at)
                    VALUES (@subject_id, @dashboard_id, @favorite, NULL)
                    ON CONFLICT (subject_id, dashboard_id) DO UPDATE SET is_favorite = EXCLUDED.is_favorite;
                    """;
            command.Parameters.AddWithValue("subject_id", subjectId.Value);
            command.Parameters.AddWithValue("dashboard_id", dashboardId.Value);
            command.Parameters.AddWithValue("favorite", favorite);
            command.Parameters.AddWithValue("now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (opened)
        {
            await using var landing = connection.CreateCommand();
            landing.Transaction = transaction;
            landing.CommandText = $"""
                INSERT INTO {DashboardMigrations.Schema}.personal_landing (subject_id, dashboard_id, changed_at)
                VALUES (@subject_id, @dashboard_id, @now)
                ON CONFLICT (subject_id) DO UPDATE
                SET dashboard_id = EXCLUDED.dashboard_id, changed_at = EXCLUDED.changed_at;
                """;
            landing.Parameters.AddWithValue("subject_id", subjectId.Value);
            landing.Parameters.AddWithValue("dashboard_id", dashboardId.Value);
            landing.Parameters.AddWithValue("now", now);
            await landing.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SetRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE \"{databaseRole}\";", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
