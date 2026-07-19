using System.Text.RegularExpressions;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;

namespace Dispatcher.Workspace;

public sealed partial class WorkspaceStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;

    public WorkspaceStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock)
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

    public Task<WorkspaceAccount?> FindAccountAsync(
        SubjectId subjectId,
        CancellationToken cancellationToken = default) =>
        QuerySingleAsync(
            $"""
            SELECT account_id, subject_id, person_id
            FROM {WorkspaceMigrations.Schema}.account
            WHERE subject_id = @subject_id;
            """,
            command => command.Parameters.AddWithValue("subject_id", subjectId.Value),
            reader => new WorkspaceAccount(
                AccountId.From(reader.GetGuid(0)),
                SubjectId.From(reader.GetGuid(1)),
                PersonId.From(reader.GetGuid(2))),
            cancellationToken);

    public Task<PersonProfile?> ReadSelfProfileAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default) =>
        ReadProfileAsync(accountId, accountId, true, cancellationToken);

    public Task<PersonProfile?> ReadVisibleProfileAsync(
        AccountId viewerAccountId,
        AccountId targetAccountId,
        CancellationToken cancellationToken = default) =>
        ReadProfileAsync(viewerAccountId, targetAccountId, false, cancellationToken);

    public async Task<WorkspacePreferences> ReadPreferencesAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default)
    {
        var result = await QuerySingleAsync(
            $"""
            SELECT compact_navigation, start_route
            FROM {WorkspaceMigrations.Schema}.preferences
            WHERE account_id = @account_id;
            """,
            command => command.Parameters.AddWithValue("account_id", accountId.Value),
            reader => new WorkspacePreferences(reader.GetBoolean(0), reader.GetString(1)),
            cancellationToken).ConfigureAwait(false);
        return result ?? new WorkspacePreferences(false, "/home");
    }

    public Task<IReadOnlyList<HomeItem>> ReadHomeItemsAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default) =>
        ReadHomeItemsCoreAsync(accountId, false, cancellationToken);

    public Task<IReadOnlyList<HomeItem>> ReadHomeItemsIncludingHiddenAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default) =>
        ReadHomeItemsCoreAsync(accountId, true, cancellationToken);

    private Task<IReadOnlyList<HomeItem>> ReadHomeItemsCoreAsync(
        AccountId accountId,
        bool includeHidden,
        CancellationToken cancellationToken) =>
        QueryListAsync(
            $"""
            SELECT DISTINCT h.item_id, h.label, h.route, h.required_permission,
                   COALESCE(o.sort_order, h.default_order) AS effective_order
            FROM {WorkspaceMigrations.Schema}.home_item h
            LEFT JOIN {WorkspaceMigrations.Schema}.account_membership m
                ON m.account_id = @account_id
               AND m.audience_kind = h.audience_kind
               AND m.audience_key = h.audience_key
            LEFT JOIN {WorkspaceMigrations.Schema}.home_override o
                ON o.account_id = @account_id AND o.item_id = h.item_id
            WHERE ((h.audience_kind = 1 AND h.audience_key = @account_key)
               OR (h.audience_kind IN (2, 3) AND m.account_id IS NOT NULL))
              AND (@include_hidden OR COALESCE(o.hidden, false) = false)
            ORDER BY effective_order, h.item_id;
            """,
            command =>
            {
                command.Parameters.AddWithValue("account_id", accountId.Value);
                command.Parameters.AddWithValue("account_key", accountId.Value.ToString("N"));
                command.Parameters.AddWithValue("include_hidden", includeHidden);
            },
            MapHomeItem,
            cancellationToken);

    public Task<IReadOnlyList<WorkspaceSearchResult>> SearchAsync(
        AccountId accountId,
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var pattern = $"%{EscapeLike(query.Trim())}%";
        return QueryListAsync(
            $"""
            SELECT kind, result_id, label, route, required_permission
            FROM (
                SELECT 'person'::text AS kind,
                       target.account_id AS result_id,
                       p.display_name AS label,
                       '/users/' || target.account_id::text AS route,
                       'workspace.profile.user.read'::text AS required_permission
                FROM {WorkspaceMigrations.Schema}.account target
                JOIN {WorkspaceMigrations.Schema}.person p ON p.person_id = target.person_id
                WHERE p.visibility = 1
                  AND lower(p.display_name) LIKE lower(@pattern) ESCAPE '\'
                  AND EXISTS (
                      SELECT 1
                      FROM {WorkspaceMigrations.Schema}.account_membership viewer_membership
                      JOIN {WorkspaceMigrations.Schema}.account_membership target_membership
                        ON target_membership.audience_kind = 3
                       AND target_membership.audience_key = viewer_membership.audience_key
                       AND target_membership.account_id = target.account_id
                      WHERE viewer_membership.account_id = @account_id
                        AND viewer_membership.audience_kind = 3)
                UNION ALL
                SELECT 'home'::text, h.item_id, h.label, h.route, h.required_permission
                FROM {WorkspaceMigrations.Schema}.home_item h
                LEFT JOIN {WorkspaceMigrations.Schema}.account_membership m
                  ON m.account_id = @account_id
                 AND m.audience_kind = h.audience_kind
                 AND m.audience_key = h.audience_key
                WHERE lower(h.label) LIKE lower(@pattern) ESCAPE '\'
                  AND ((h.audience_kind = 1 AND h.audience_key = @account_key)
                    OR (h.audience_kind IN (2, 3) AND m.account_id IS NOT NULL))
            ) results
            ORDER BY lower(label), result_id;
            """,
            command =>
            {
                command.Parameters.AddWithValue("account_id", accountId.Value);
                command.Parameters.AddWithValue("account_key", accountId.Value.ToString("N"));
                command.Parameters.AddWithValue("pattern", pattern);
            },
            reader => new WorkspaceSearchResult(
                reader.GetString(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)),
            cancellationToken);
    }

    public Task<IReadOnlyList<HomeItem>> ReadFavoritesAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default) =>
        ReadTrackedItemsAsync("favorite", "created_at", accountId, cancellationToken);

    public Task<IReadOnlyList<HomeItem>> ReadRecentAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default) =>
        ReadTrackedItemsAsync("recent", "opened_at", accountId, cancellationToken);

    public Task UpdateProfileAsync(
        AuthorizedAccess authorization,
        AccountId accountId,
        ProfileSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.DisplayName);
        return ExecuteMutationAsync(
            authorization,
            accountId,
            "profile.update",
            $"""
            UPDATE {WorkspaceMigrations.Schema}.person p
            SET display_name = @display_name,
                title = @title,
                availability = @availability,
                visibility = @visibility
            FROM {WorkspaceMigrations.Schema}.account a
            WHERE a.account_id = @account_id AND a.person_id = p.person_id;
            """,
            command =>
            {
                command.Parameters.AddWithValue("display_name", settings.DisplayName.Trim());
                command.Parameters.AddWithValue("title", (object?)settings.Title?.Trim() ?? DBNull.Value);
                command.Parameters.AddWithValue("availability", (short)settings.Availability);
                command.Parameters.AddWithValue("visibility", (short)settings.Visibility);
            },
            cancellationToken);
    }

    public Task UpdatePreferencesAsync(
        AuthorizedAccess authorization,
        AccountId accountId,
        WorkspacePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.StartRoute.Length == 0 || preferences.StartRoute[0] != '/')
        {
            throw new ArgumentException("Start route must be application-relative.", nameof(preferences));
        }

        return ExecuteMutationAsync(
            authorization,
            accountId,
            "preferences.update",
            $"""
            INSERT INTO {WorkspaceMigrations.Schema}.preferences
                (account_id, compact_navigation, start_route, version)
            VALUES (@account_id, @compact_navigation, @start_route, 1)
            ON CONFLICT (account_id) DO UPDATE
            SET compact_navigation = EXCLUDED.compact_navigation,
                start_route = EXCLUDED.start_route,
                version = {WorkspaceMigrations.Schema}.preferences.version + 1;
            """,
            command =>
            {
                command.Parameters.AddWithValue("compact_navigation", preferences.CompactNavigation);
                command.Parameters.AddWithValue("start_route", preferences.StartRoute);
            },
            cancellationToken);
    }

    public Task SetHomeOverrideAsync(
        AuthorizedAccess authorization,
        AccountId accountId,
        HomeItemId itemId,
        bool hidden,
        int? order,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(
            authorization,
            accountId,
            "home.override",
            $"""
            INSERT INTO {WorkspaceMigrations.Schema}.home_override
                (account_id, item_id, hidden, sort_order)
            VALUES (@account_id, @item_id, @hidden, @sort_order)
            ON CONFLICT (account_id, item_id) DO UPDATE
            SET hidden = EXCLUDED.hidden, sort_order = EXCLUDED.sort_order;
            """,
            command =>
            {
                command.Parameters.AddWithValue("item_id", itemId.Value);
                command.Parameters.AddWithValue("hidden", hidden);
                command.Parameters.AddWithValue("sort_order", (object?)order ?? DBNull.Value);
            },
            cancellationToken);

    public Task SetFavoriteAsync(
        AuthorizedAccess authorization,
        AccountId accountId,
        HomeItemId itemId,
        bool favorite,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(
            authorization,
            accountId,
            "favorite.update",
            favorite
                ? $"""
                  INSERT INTO {WorkspaceMigrations.Schema}.favorite (account_id, item_id, created_at)
                  VALUES (@account_id, @item_id, @changed_at)
                  ON CONFLICT DO NOTHING;
                  """
                : $"DELETE FROM {WorkspaceMigrations.Schema}.favorite WHERE account_id = @account_id AND item_id = @item_id;",
            command => command.Parameters.AddWithValue("item_id", itemId.Value),
            cancellationToken);

    public Task RecordRecentAsync(
        AuthorizedAccess authorization,
        AccountId accountId,
        HomeItemId itemId,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(
            authorization,
            accountId,
            "recent.open",
            $"""
            INSERT INTO {WorkspaceMigrations.Schema}.recent (account_id, item_id, opened_at)
            VALUES (@account_id, @item_id, @changed_at)
            ON CONFLICT (account_id, item_id) DO UPDATE SET opened_at = EXCLUDED.opened_at;
            """,
            command => command.Parameters.AddWithValue("item_id", itemId.Value),
            cancellationToken);

    private Task<PersonProfile?> ReadProfileAsync(
        AccountId viewerAccountId,
        AccountId targetAccountId,
        bool self,
        CancellationToken cancellationToken) =>
        QuerySingleAsync(
            $"""
            SELECT target.account_id, p.person_id, p.display_name, p.title, p.availability, p.visibility
            FROM {WorkspaceMigrations.Schema}.account target
            JOIN {WorkspaceMigrations.Schema}.person p ON p.person_id = target.person_id
            WHERE target.account_id = @target_account_id
              AND (@self OR (p.visibility = 1 AND EXISTS (
                    SELECT 1
                    FROM {WorkspaceMigrations.Schema}.account_membership viewer_membership
                    JOIN {WorkspaceMigrations.Schema}.account_membership target_membership
                      ON target_membership.audience_kind = 3
                     AND target_membership.audience_key = viewer_membership.audience_key
                     AND target_membership.account_id = target.account_id
                    WHERE viewer_membership.account_id = @viewer_account_id
                      AND viewer_membership.audience_kind = 3)));
            """,
            command =>
            {
                command.Parameters.AddWithValue("viewer_account_id", viewerAccountId.Value);
                command.Parameters.AddWithValue("target_account_id", targetAccountId.Value);
                command.Parameters.AddWithValue("self", self);
            },
            reader => new PersonProfile(
                AccountId.From(reader.GetGuid(0)),
                PersonId.From(reader.GetGuid(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                (PersonAvailability)reader.GetInt16(4),
                (ProfileVisibility)reader.GetInt16(5)),
            cancellationToken);

    private Task<IReadOnlyList<HomeItem>> ReadTrackedItemsAsync(
        string table,
        string orderColumn,
        AccountId accountId,
        CancellationToken cancellationToken) =>
        QueryListAsync(
            $"""
            SELECT h.item_id, h.label, h.route, h.required_permission, h.default_order
            FROM {WorkspaceMigrations.Schema}.{table} tracked
            JOIN {WorkspaceMigrations.Schema}.home_item h ON h.item_id = tracked.item_id
            WHERE tracked.account_id = @account_id
            ORDER BY tracked.{orderColumn} DESC, h.item_id;
            """,
            command => command.Parameters.AddWithValue("account_id", accountId.Value),
            MapHomeItem,
            cancellationToken);

    private async Task ExecuteMutationAsync(
        AuthorizedAccess authorization,
        AccountId accountId,
        string action,
        string sql,
        Action<NpgsqlCommand> addParameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var changedAt = clock.GetUtcNow();

        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("account_id", accountId.Value);
            command.Parameters.AddWithValue("changed_at", changedAt);
            addParameters(command);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var audit = new NpgsqlCommand(
                         $"""
                         INSERT INTO {WorkspaceMigrations.Schema}.preference_audit
                             (audit_id, account_id, session_id, subject_id, action, changed_at)
                         VALUES (@audit_id, @account_id, @session_id, @subject_id, @action, @changed_at);
                         """,
                         connection,
                         transaction))
        {
            audit.Parameters.AddWithValue("audit_id", Guid.CreateVersion7());
            audit.Parameters.AddWithValue("account_id", accountId.Value);
            audit.Parameters.AddWithValue("session_id", authorization.Session.Id.Value);
            audit.Parameters.AddWithValue("subject_id", authorization.Session.SubjectId.Value);
            audit.Parameters.AddWithValue("action", action);
            audit.Parameters.AddWithValue("changed_at", changedAt);
            await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> QuerySingleAsync<T>(
        string sql,
        Action<NpgsqlCommand> addParameters,
        Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken)
        where T : class
    {
        var items = await QueryListAsync(sql, addParameters, map, cancellationToken).ConfigureAwait(false);
        return items.SingleOrDefault();
    }

    private async Task<IReadOnlyList<T>> QueryListAsync<T>(
        string sql,
        Action<NpgsqlCommand> addParameters,
        Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        addParameters(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(map(reader));
        }

        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return items;
    }

    private async Task SetRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE \"{databaseRole}\";", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static HomeItem MapHomeItem(NpgsqlDataReader reader) => new(
        HomeItemId.From(reader.GetGuid(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt32(4));

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
