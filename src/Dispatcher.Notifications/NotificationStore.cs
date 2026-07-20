using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Npgsql;

namespace Dispatcher.Notifications;

public sealed record NotificationAcceptance(
    IReadOnlyList<NotificationRoute> Routes,
    IReadOnlyList<NotificationInboxItem> InboxItems);

public sealed class NotificationStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;

    public NotificationStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        ArgumentNullException.ThrowIfNull(clock);
        if (databaseRole.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new ArgumentException("Invalid PostgreSQL role name.", nameof(databaseRole));
        }

        this.dataSource = dataSource;
        this.databaseRole = databaseRole;
        this.clock = clock;
    }

    public async Task<Result<NotificationPolicy>> SavePolicyAsync(
        NotificationPolicy policy,
        RevisionNumber? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var dto = PolicyDto.From(policy);
        var json = JsonSerializer.Serialize(dto);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var existing = await ReadVersionAsync(
            connection,
            transaction,
            "policy",
            "scope_id",
            policy.ScopeId.Value,
            "revision",
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            if (expectedRevision is not null || policy.Revision != RevisionNumber.Initial)
            {
                return Failure<NotificationPolicy>("notification.policy_version", "Initial policy revision is invalid.");
            }
        }
        else if (expectedRevision?.Value != existing || policy.Revision.Value != checked(existing.Value + 1))
        {
            return Failure<NotificationPolicy>("notification.policy_version", "Notification policy revision is stale.");
        }

        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {NotificationMigrations.Schema}.policy
                (scope_id, policy_id, revision, fingerprint, snapshot, updated_at)
            VALUES (@scope_id, @policy_id, @revision, @fingerprint, CAST(@snapshot AS jsonb), @updated_at)
            ON CONFLICT (scope_id) DO UPDATE SET
                policy_id = EXCLUDED.policy_id,
                revision = EXCLUDED.revision,
                fingerprint = EXCLUDED.fingerprint,
                snapshot = EXCLUDED.snapshot,
                updated_at = EXCLUDED.updated_at;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", policy.ScopeId.Value);
        command.Parameters.AddWithValue("policy_id", policy.PolicyId.Value);
        command.Parameters.AddWithValue("revision", checked((long)policy.Revision.Value));
        command.Parameters.AddWithValue("fingerprint", Fingerprint(json));
        command.Parameters.AddWithValue("snapshot", json);
        command.Parameters.AddWithValue("updated_at", clock.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(policy);
    }

    public async Task<NotificationPolicy?> ReadPolicyAsync(
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT snapshot::text FROM {NotificationMigrations.Schema}.policy WHERE scope_id = @scope_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : JsonSerializer.Deserialize<PolicyDto>(json)!.ToModel();
    }

    public async Task<Result<PersonalNotificationSettings>> SaveSettingsAsync(
        PersonalNotificationSettings settings,
        StateVersion? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(SettingsDto.From(settings));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var existing = await ReadVersionAsync(
            connection,
            transaction,
            "personal_setting",
            "person_id",
            settings.PersonId.Value,
            "version",
            cancellationToken).ConfigureAwait(false);
        var version = ValidateStateVersion(existing, expectedVersion, settings.Version, "settings");
        if (version.IsFailure)
        {
            return Result.Failure<PersonalNotificationSettings>(version.Error!);
        }

        await UpsertSnapshotAsync(
            connection,
            transaction,
            "personal_setting",
            "person_id",
            settings.PersonId.Value,
            settings.Version.Value,
            json,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(settings);
    }

    public async Task<Result<NotificationSubscription>> SaveSubscriptionAsync(
        NotificationSubscription subscription,
        StateVersion? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var json = JsonSerializer.Serialize(SubscriptionDto.From(subscription));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var existing = await ReadVersionAsync(
            connection,
            transaction,
            "subscription",
            "subscription_id",
            subscription.SubscriptionId.Value,
            "version",
            cancellationToken).ConfigureAwait(false);
        var version = ValidateStateVersion(existing, expectedVersion, subscription.Version, "subscription");
        if (version.IsFailure)
        {
            return Result.Failure<NotificationSubscription>(version.Error!);
        }

        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {NotificationMigrations.Schema}.subscription
                (subscription_id, person_id, scope_id, version, fingerprint, snapshot, updated_at)
            VALUES (@id, @person_id, @scope_id, @version, @fingerprint, CAST(@snapshot AS jsonb), @updated_at)
            ON CONFLICT (subscription_id) DO UPDATE SET
                person_id = EXCLUDED.person_id,
                scope_id = EXCLUDED.scope_id,
                version = EXCLUDED.version,
                fingerprint = EXCLUDED.fingerprint,
                snapshot = EXCLUDED.snapshot,
                updated_at = EXCLUDED.updated_at;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", subscription.SubscriptionId.Value);
        command.Parameters.AddWithValue("person_id", subscription.PersonId.Value);
        command.Parameters.AddWithValue("scope_id", subscription.ScopeId.Value);
        command.Parameters.AddWithValue("version", checked((long)subscription.Version.Value));
        command.Parameters.AddWithValue("fingerprint", Fingerprint(json));
        command.Parameters.AddWithValue("snapshot", json);
        command.Parameters.AddWithValue("updated_at", clock.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(subscription);
    }

    public async Task<IReadOnlyList<NotificationSubscription>> ReadSubscriptionsAsync(
        RuntimeScopeId scopeId,
        CancellationToken cancellationToken = default) =>
        await ReadSnapshotsAsync(
            $"SELECT snapshot::text FROM {NotificationMigrations.Schema}.subscription WHERE scope_id = @value ORDER BY subscription_id;",
            scopeId.Value,
            json => JsonSerializer.Deserialize<SubscriptionDto>(json)!.ToModel(),
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<PersonId, PersonalNotificationSettings>> ReadSettingsAsync(
        IReadOnlyCollection<PersonId> personIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(personIds);
        if (personIds.Count == 0)
        {
            return new Dictionary<PersonId, PersonalNotificationSettings>();
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT snapshot::text FROM {NotificationMigrations.Schema}.personal_setting WHERE person_id = ANY(@ids);",
            connection,
            transaction);
        command.Parameters.AddWithValue("ids", personIds.Select(person => person.Value).Distinct().ToArray());
        var values = new Dictionary<PersonId, PersonalNotificationSettings>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var value = JsonSerializer.Deserialize<SettingsDto>(reader.GetString(0))!.ToModel();
            values.Add(value.PersonId, value);
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return values;
    }

    public async Task<Result<NotificationAcceptance>> AcceptAsync(
        NotificationCandidate candidate,
        IReadOnlyList<NotificationRoute> routes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(routes);
        var inboxRoutes = routes.Where(route => route.Channel == NotificationChannel.Inbox).ToArray();
        var items = new List<NotificationInboxItem>(inboxRoutes.Length);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        foreach (var route in inboxRoutes)
        {
            var item = new NotificationInboxItem(
                NotificationInboxItemId.New(),
                route.RecipientId,
                candidate.EventId,
                candidate.Title,
                candidate.Priority,
                candidate.SourceLink,
                clock.GetUtcNow(),
                null,
                StateVersion.Initial);
            var dto = InboxDto.From(item);
            var json = JsonSerializer.Serialize(dto);
            var fingerprint = AcceptanceFingerprint(candidate, route.RecipientId);
            await using var insert = new NpgsqlCommand(
                $"""
                INSERT INTO {NotificationMigrations.Schema}.inbox_item
                    (recipient_id, item_id, event_id, version, read_at, fingerprint, snapshot, created_at)
                VALUES (@recipient_id, @item_id, @event_id, 1, NULL, @fingerprint, CAST(@snapshot AS jsonb), @created_at)
                ON CONFLICT (recipient_id, event_id) DO NOTHING;
                """,
                connection,
                transaction);
            insert.Parameters.AddWithValue("recipient_id", item.RecipientId.Value);
            insert.Parameters.AddWithValue("item_id", item.ItemId.Value);
            insert.Parameters.AddWithValue("event_id", item.EventId.Value);
            insert.Parameters.AddWithValue("fingerprint", fingerprint);
            insert.Parameters.AddWithValue("snapshot", json);
            insert.Parameters.AddWithValue("created_at", item.CreatedAt);
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (inserted == 0)
            {
                var existing = await ReadInboxItemAsync(
                    connection,
                    transaction,
                    route.RecipientId,
                    candidate.EventId,
                    cancellationToken).ConfigureAwait(false);
                if (existing is null || existing.Value.Fingerprint != fingerprint)
                {
                    return Failure<NotificationAcceptance>(
                        "notification.acceptance_conflict",
                        "Notification event was already accepted with different content.");
                }

                items.Add(existing.Value.Item);
            }
            else
            {
                items.Add(item);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new NotificationAcceptance(routes, items));
    }

    public async Task<IReadOnlyList<NotificationInboxItem>> ReadInboxAsync(
        PersonId recipientId,
        CancellationToken cancellationToken = default) =>
        await ReadSnapshotsAsync(
            $"SELECT snapshot::text FROM {NotificationMigrations.Schema}.inbox_item WHERE recipient_id = @value ORDER BY created_at DESC, item_id;",
            recipientId.Value,
            json => JsonSerializer.Deserialize<InboxDto>(json)!.ToModel(),
            cancellationToken).ConfigureAwait(false);

    public async Task<NotificationInboxItem?> ReadInboxItemAsync(
        PersonId recipientId,
        NotificationInboxItemId itemId,
        CancellationToken cancellationToken = default)
    {
        var values = await ReadSnapshotsAsync(
            $"SELECT snapshot::text FROM {NotificationMigrations.Schema}.inbox_item WHERE recipient_id = @value AND item_id = @item_id;",
            recipientId.Value,
            json => JsonSerializer.Deserialize<InboxDto>(json)!.ToModel(),
            cancellationToken,
            itemId.Value).ConfigureAwait(false);
        return values.SingleOrDefault();
    }

    public async Task<Result<NotificationInboxItem>> MarkReadAsync(
        PersonId recipientId,
        NotificationInboxItemId itemId,
        StateVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var select = new NpgsqlCommand(
            $"SELECT snapshot::text FROM {NotificationMigrations.Schema}.inbox_item WHERE recipient_id = @recipient_id AND item_id = @item_id FOR UPDATE;",
            connection,
            transaction);
        select.Parameters.AddWithValue("recipient_id", recipientId.Value);
        select.Parameters.AddWithValue("item_id", itemId.Value);
        var json = (string?)await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return Failure<NotificationInboxItem>("notification.inbox_not_found", "Inbox item was not found.");
        }

        var current = JsonSerializer.Deserialize<InboxDto>(json)!.ToModel();
        if (current.Version != expectedVersion)
        {
            return Failure<NotificationInboxItem>("notification.inbox_version", "Inbox item version is stale.");
        }

        if (current.ReadAt is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(current);
        }

        var updated = current with { ReadAt = clock.GetUtcNow(), Version = current.Version.Next() };
        var updatedJson = JsonSerializer.Serialize(InboxDto.From(updated));
        await using var update = new NpgsqlCommand(
            $"""
            UPDATE {NotificationMigrations.Schema}.inbox_item
            SET version = @version, read_at = @read_at, snapshot = CAST(@snapshot AS jsonb)
            WHERE recipient_id = @recipient_id AND item_id = @item_id;
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("version", checked((long)updated.Version.Value));
        update.Parameters.AddWithValue("read_at", updated.ReadAt.Value);
        update.Parameters.AddWithValue("snapshot", updatedJson);
        update.Parameters.AddWithValue("recipient_id", recipientId.Value);
        update.Parameters.AddWithValue("item_id", itemId.Value);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(updated);
    }

    private async Task<IReadOnlyList<T>> ReadSnapshotsAsync<T>(
        string sql,
        Guid value,
        Func<string, T> map,
        CancellationToken cancellationToken,
        Guid? itemId = null)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("value", value);
        if (itemId is not null)
        {
            command.Parameters.AddWithValue("item_id", itemId.Value);
        }

        var values = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(map(reader.GetString(0)));
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return values;
    }

    private static async Task<ulong?> ReadVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        string keyColumn,
        Guid key,
        string versionColumn,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT {versionColumn} FROM {NotificationMigrations.Schema}.{table} WHERE {keyColumn} = @key FOR UPDATE;",
            connection,
            transaction);
        command.Parameters.AddWithValue("key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : checked((ulong)(long)value);
    }

    private async Task UpsertSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string table,
        string keyColumn,
        Guid key,
        ulong version,
        string json,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {NotificationMigrations.Schema}.{table}
                ({keyColumn}, version, fingerprint, snapshot, updated_at)
            VALUES (@key, @version, @fingerprint, CAST(@snapshot AS jsonb), @updated_at)
            ON CONFLICT ({keyColumn}) DO UPDATE SET
                version = EXCLUDED.version,
                fingerprint = EXCLUDED.fingerprint,
                snapshot = EXCLUDED.snapshot,
                updated_at = EXCLUDED.updated_at;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("version", checked((long)version));
        command.Parameters.AddWithValue("fingerprint", Fingerprint(json));
        command.Parameters.AddWithValue("snapshot", json);
        command.Parameters.AddWithValue("updated_at", clock.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Result ValidateStateVersion(
        ulong? existing,
        StateVersion? expected,
        StateVersion proposed,
        string target)
    {
        if (existing is null)
        {
            return expected is null && proposed == StateVersion.Initial
                ? Result.Success()
                : Failure("notification.version", $"Initial {target} version is invalid.");
        }

        return expected?.Value == existing && proposed.Value == checked(existing.Value + 1)
            ? Result.Success()
            : Failure("notification.version", $"{target} version is stale.");
    }

    private static async Task<(NotificationInboxItem Item, string Fingerprint)?> ReadInboxItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PersonId recipientId,
        OperationalEventId eventId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT snapshot::text, fingerprint FROM {NotificationMigrations.Schema}.inbox_item WHERE recipient_id = @recipient_id AND event_id = @event_id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("recipient_id", recipientId.Value);
        command.Parameters.AddWithValue("event_id", eventId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (JsonSerializer.Deserialize<InboxDto>(reader.GetString(0))!.ToModel(), reader.GetString(1))
            : null;
    }

    private async Task SetRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE \"{databaseRole}\";", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Fingerprint(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private static string AcceptanceFingerprint(NotificationCandidate candidate, PersonId recipientId) =>
        Fingerprint(JsonSerializer.Serialize(new
        {
            RecipientId = recipientId.Value,
            EventId = candidate.EventId.Value,
            candidate.Title,
            Priority = (int)candidate.Priority,
            Source = SourceLinkDto.From(candidate.SourceLink),
        }));

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));

    private sealed record RuleDto(Guid RuleId, int MinimumPriority, Guid[] Recipients, int[] Channels)
    {
        public MandatoryNotificationRule ToModel() => new(
            NotificationRuleId.From(RuleId),
            (AlarmPriority)MinimumPriority,
            Recipients.Select(PersonId.From).ToArray(),
            Channels.Select(value => (NotificationChannel)value).ToArray());
    }

    private sealed record PolicyDto(Guid PolicyId, Guid ScopeId, ulong Revision, RuleDto[] Rules)
    {
        public static PolicyDto From(NotificationPolicy value) => new(
            value.PolicyId.Value,
            value.ScopeId.Value,
            value.Revision.Value,
            value.MandatoryRules.Select(rule => new RuleDto(
                rule.RuleId.Value,
                (int)rule.MinimumPriority,
                rule.Recipients.Select(person => person.Value).ToArray(),
                rule.Channels.Select(channel => (int)channel).ToArray())).ToArray());

        public NotificationPolicy ToModel() => new(
            NotificationPolicyId.From(PolicyId),
            RuntimeScopeId.From(ScopeId),
            RevisionNumber.From(Revision),
            Rules.Select(rule => rule.ToModel()).ToArray());
    }

    private sealed record ScheduleDto(int[] Days, TimeOnly StartsAtUtc, TimeOnly EndsAtUtc)
    {
        public static ScheduleDto From(NotificationSchedule value) => new(
            value.ActiveDays.Select(day => (int)day).ToArray(), value.StartsAtUtc, value.EndsAtUtc);
        public NotificationSchedule ToModel() => new(Days.Select(day => (DayOfWeek)day).ToArray(), StartsAtUtc, EndsAtUtc);
    }

    private sealed record AbsenceDto(DateTimeOffset StartsAt, DateTimeOffset EndsAt, Guid CoveragePersonId);

    private sealed record SettingsDto(
        Guid PersonId,
        ulong Version,
        ScheduleDto? Schedule,
        ScheduleDto[] QuietPeriods,
        AbsenceDto? Absence,
        Dictionary<int, bool> Channels)
    {
        public static SettingsDto From(PersonalNotificationSettings value) => new(
            value.PersonId.Value,
            value.Version.Value,
            value.Schedule is null ? null : ScheduleDto.From(value.Schedule),
            value.QuietPeriods.Select(period => ScheduleDto.From(period.Schedule)).ToArray(),
            value.Absence is null ? null : new AbsenceDto(
                value.Absence.StartsAt, value.Absence.EndsAt, value.Absence.CoveragePersonId.Value),
            value.ChannelPreferences.ToDictionary(item => (int)item.Channel, item => item.Enabled));

        public PersonalNotificationSettings ToModel() => new(
            global::Dispatcher.Workspace.PersonId.From(PersonId),
            StateVersion.From(Version),
            Schedule?.ToModel(),
            QuietPeriods.Select(period => new NotificationQuietPeriod(period.ToModel())).ToArray(),
            Absence is null ? null : new NotificationAbsence(
                Absence.StartsAt,
                Absence.EndsAt,
                global::Dispatcher.Workspace.PersonId.From(Absence.CoveragePersonId)),
            Channels.Select(item => new NotificationChannelPreference((NotificationChannel)item.Key, item.Value)).ToArray());
    }

    private sealed record SubscriptionDto(
        Guid SubscriptionId,
        Guid PersonId,
        Guid ScopeId,
        ulong Version,
        int MinimumPriority,
        Guid[] PointIds,
        int[] Channels,
        bool Enabled)
    {
        public static SubscriptionDto From(NotificationSubscription value) => new(
            value.SubscriptionId.Value,
            value.PersonId.Value,
            value.ScopeId.Value,
            value.Version.Value,
            (int)value.MinimumPriority,
            value.PointIds.Select(point => point.Value).ToArray(),
            value.Channels.Select(channel => (int)channel).ToArray(),
            value.Enabled);

        public NotificationSubscription ToModel() => new(
            NotificationSubscriptionId.From(SubscriptionId),
            global::Dispatcher.Workspace.PersonId.From(PersonId),
            RuntimeScopeId.From(ScopeId),
            StateVersion.From(Version),
            (AlarmPriority)MinimumPriority,
            PointIds.Select(PointId.From).ToArray(),
            Channels.Select(value => (NotificationChannel)value).ToArray(),
            Enabled);
    }

    private sealed record SourceLinkDto(
        Guid EventId,
        Guid OccurrenceId,
        Guid ScopeId,
        Guid PointId,
        string Route,
        string[] RequiredPermissions)
    {
        public static SourceLinkDto From(NotificationSourceLink value) => new(
            value.EventId.Value,
            value.OccurrenceId.Value,
            value.ScopeId.Value,
            value.PointId.Value,
            value.Route,
            value.RequiredPermissions.Select(permission => permission.Value).ToArray());

        public NotificationSourceLink ToModel() => new(
            OperationalEventId.From(EventId),
            AlarmOccurrenceId.From(OccurrenceId),
            RuntimeScopeId.From(ScopeId),
            global::Dispatcher.Semantics.PointId.From(PointId),
            Route,
            RequiredPermissions.Select(PermissionCode.From).ToArray());
    }

    private sealed record InboxDto(
        Guid ItemId,
        Guid RecipientId,
        Guid EventId,
        string Title,
        int Priority,
        SourceLinkDto SourceLink,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ReadAt,
        ulong Version)
    {
        public static InboxDto From(NotificationInboxItem value) => new(
            value.ItemId.Value,
            value.RecipientId.Value,
            value.EventId.Value,
            value.Title,
            (int)value.Priority,
            SourceLinkDto.From(value.SourceLink),
            value.CreatedAt,
            value.ReadAt,
            value.Version.Value);

        public NotificationInboxItem ToModel() => new(
            NotificationInboxItemId.From(ItemId),
            global::Dispatcher.Workspace.PersonId.From(RecipientId),
            OperationalEventId.From(EventId),
            Title,
            (AlarmPriority)Priority,
            SourceLink.ToModel(),
            CreatedAt,
            ReadAt,
            StateVersion.From(Version));
    }
}
