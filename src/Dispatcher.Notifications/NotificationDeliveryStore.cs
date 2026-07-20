using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dispatcher.Events;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Npgsql;

namespace Dispatcher.Notifications;

public sealed class NotificationDeliveryStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;

    public NotificationDeliveryStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock)
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

    public async Task<Result<IReadOnlyList<NotificationDeliveryObligation>>> EnsureObligationsAsync(
        NotificationCandidate candidate,
        IReadOnlyList<NotificationRoute> routes,
        IReadOnlyDictionary<PersonId, PersonalNotificationSettings> settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(settings);
        var emailRoutes = routes.Where(route => route.Channel == NotificationChannel.Email).ToArray();
        var obligations = new List<NotificationDeliveryObligation>(emailRoutes.Length);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        foreach (var route in emailRoutes)
        {
            if (!settings.TryGetValue(route.RecipientId, out var recipient) ||
                recipient.EmailAddress is null || !MailAddress.TryCreate(recipient.EmailAddress, out _))
            {
                return Failure<IReadOnlyList<NotificationDeliveryObligation>>(
                    "notification.email_missing",
                    "Email route recipient has no valid notification endpoint.");
            }

            var obligation = new NotificationDeliveryObligation(
                NotificationDeliveryId.New(),
                candidate.EventId,
                route.RecipientId,
                route.CoveredPersonId,
                route.Mandatory,
                recipient.EmailAddress,
                candidate.Title,
                $"{candidate.Title}\nOpen in Dispatcher: {candidate.SourceLink.Route}",
                NotificationDeliveryStatus.Pending,
                0,
                clock.GetUtcNow(),
                null,
                null,
                null,
                StateVersion.Initial,
                clock.GetUtcNow(),
                null);
            var fingerprint = Fingerprint(obligation);
            await using var insert = new NpgsqlCommand(
                $"""
                INSERT INTO {NotificationMigrations.Schema}.delivery_obligation
                    (delivery_id, event_id, recipient_id, covered_person_id, mandatory, channel, endpoint,
                     subject, body, status, attempt_count, next_attempt_at, active_attempt_id, lease_owner,
                     lease_until, version, fingerprint, created_at, completed_at)
                VALUES
                    (@delivery_id, @event_id, @recipient_id, @covered_person_id, @mandatory, 2, @endpoint,
                     @subject, @body, 1, 0, @next_attempt_at, NULL, NULL, NULL, 1, @fingerprint, @created_at, NULL)
                ON CONFLICT (event_id, recipient_id, channel) DO NOTHING;
                """,
                connection,
                transaction);
            AddObligationParameters(insert, obligation, fingerprint);
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (inserted == 0)
            {
                var existing = await ReadByEventRecipientAsync(
                    connection,
                    transaction,
                    candidate.EventId,
                    route.RecipientId,
                    cancellationToken).ConfigureAwait(false);
                if (existing is null || !string.Equals(existing.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return Failure<IReadOnlyList<NotificationDeliveryObligation>>(
                        "notification.delivery_conflict",
                        "Delivery obligation was already accepted with different content.");
                }

                obligations.Add(existing.Value.Obligation);
            }
            else
            {
                obligations.Add(obligation);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<NotificationDeliveryObligation>>(obligations);
    }

    public async Task<Result<NotificationDeliveryClaimResult>> ClaimAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        if (workerId.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }

        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var select = new NpgsqlCommand(
            $"""
            SELECT delivery_id, event_id, recipient_id, covered_person_id, mandatory, endpoint, subject, body,
                   status, attempt_count, next_attempt_at, active_attempt_id, lease_owner, lease_until,
                   version, created_at, completed_at
            FROM {NotificationMigrations.Schema}.delivery_obligation
            WHERE (status IN (1, 3) AND next_attempt_at <= @now)
               OR (status = 2 AND lease_until <= @now)
            ORDER BY next_attempt_at, created_at, delivery_id
            FOR UPDATE SKIP LOCKED
            LIMIT 1;
            """,
            connection,
            transaction);
        select.Parameters.AddWithValue("now", now);
        NotificationDeliveryObligation? obligation;
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            obligation = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapObligation(reader) : null;
        }

        if (obligation is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(new NotificationDeliveryClaimResult(null));
        }

        NotificationProviderAttempt attempt;
        if (obligation.Status == NotificationDeliveryStatus.InFlight && obligation.ActiveAttemptId is { } activeAttemptId)
        {
            attempt = await ReadAttemptAsync(
                connection,
                transaction,
                activeAttemptId,
                false,
                cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Active attempt is missing.");
        }
        else
        {
            var attemptId = NotificationAttemptId.New();
            var attemptNumber = checked(obligation.AttemptCount + 1);
            attempt = new NotificationProviderAttempt(
                attemptId,
                obligation.DeliveryId,
                attemptNumber,
                NotificationAttemptOutcome.InFlight,
                null,
                null,
                now,
                null);
            await using var insertAttempt = new NpgsqlCommand(
                $"""
                INSERT INTO {NotificationMigrations.Schema}.provider_attempt
                    (attempt_id, delivery_id, attempt_number, outcome, started_at)
                VALUES (@attempt_id, @delivery_id, @attempt_number, 1, @started_at);
                """,
                connection,
                transaction);
            insertAttempt.Parameters.AddWithValue("attempt_id", attempt.AttemptId.Value);
            insertAttempt.Parameters.AddWithValue("delivery_id", attempt.DeliveryId.Value);
            insertAttempt.Parameters.AddWithValue("attempt_number", attempt.AttemptNumber);
            insertAttempt.Parameters.AddWithValue("started_at", attempt.StartedAt);
            await insertAttempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            obligation = obligation with
            {
                AttemptCount = attemptNumber,
                ActiveAttemptId = attemptId,
            };
        }

        obligation = obligation with
        {
            Status = NotificationDeliveryStatus.InFlight,
            LeaseOwner = workerId,
            LeaseUntil = now.Add(leaseDuration),
            Version = obligation.Version.Next(),
        };
        await UpdateObligationAsync(connection, transaction, obligation, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new NotificationDeliveryClaimResult(
            new NotificationDeliveryClaim(obligation, attempt)));
    }

    public async Task<Result<NotificationDeliveryObligation>> CompleteAsync(
        string workerId,
        NotificationAttemptId attemptId,
        NotificationProviderResult providerResult,
        NotificationDeliveryPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(providerResult);
        ArgumentNullException.ThrowIfNull(policy);
        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var attempt = await ReadAttemptAsync(connection, transaction, attemptId, true, cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            return Failure<NotificationDeliveryObligation>("notification.attempt_not_found", "Provider attempt was not found.");
        }

        var obligation = await ReadByIdAsync(
            connection,
            transaction,
            attempt.DeliveryId,
            true,
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Delivery obligation is missing.");
        if (attempt.Outcome != NotificationAttemptOutcome.InFlight)
        {
            if (attempt.Outcome == providerResult.Outcome &&
                string.Equals(attempt.OutcomeCode, providerResult.OutcomeCode, StringComparison.Ordinal) &&
                string.Equals(attempt.ProviderReceipt, providerResult.ProviderReceipt, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Result.Success(obligation);
            }

            return Failure<NotificationDeliveryObligation>(
                "notification.attempt_conflict",
                "Provider attempt already has another accepted outcome.");
        }

        if (obligation.Status != NotificationDeliveryStatus.InFlight ||
            obligation.ActiveAttemptId != attemptId ||
            !string.Equals(obligation.LeaseOwner, workerId, StringComparison.Ordinal) ||
            obligation.LeaseUntil <= now)
        {
            return Failure<NotificationDeliveryObligation>(
                "notification.attempt_lease",
                "Provider attempt lease is not active for this worker.");
        }

        var terminal = false;
        var status = providerResult.Outcome switch
        {
            NotificationAttemptOutcome.Delivered => NotificationDeliveryStatus.Delivered,
            NotificationAttemptOutcome.PermanentFailure => obligation.Mandatory
                ? NotificationDeliveryStatus.EscalationRequired
                : NotificationDeliveryStatus.TerminalFailure,
            NotificationAttemptOutcome.TransientFailure when attempt.AttemptNumber < policy.MaxAttempts =>
                NotificationDeliveryStatus.PendingRetry,
            NotificationAttemptOutcome.TransientFailure => obligation.Mandatory
                ? NotificationDeliveryStatus.EscalationRequired
                : NotificationDeliveryStatus.TerminalFailure,
            _ => throw new ArgumentOutOfRangeException(nameof(providerResult)),
        };
        terminal = status is NotificationDeliveryStatus.Delivered or
            NotificationDeliveryStatus.EscalationRequired or NotificationDeliveryStatus.TerminalFailure;
        var nextAttemptAt = status == NotificationDeliveryStatus.PendingRetry
            ? now.Add(policy.DelayAfter(attempt.AttemptNumber))
            : obligation.NextAttemptAt;
        obligation = obligation with
        {
            Status = status,
            NextAttemptAt = nextAttemptAt,
            ActiveAttemptId = null,
            LeaseOwner = null,
            LeaseUntil = null,
            Version = obligation.Version.Next(),
            CompletedAt = terminal ? now : null,
        };
        await using var updateAttempt = new NpgsqlCommand(
            $"""
            UPDATE {NotificationMigrations.Schema}.provider_attempt
            SET outcome = @outcome, outcome_code = @outcome_code,
                provider_receipt = @provider_receipt, completed_at = @completed_at
            WHERE attempt_id = @attempt_id;
            """,
            connection,
            transaction);
        updateAttempt.Parameters.AddWithValue("outcome", (short)providerResult.Outcome);
        updateAttempt.Parameters.AddWithValue("outcome_code", providerResult.OutcomeCode);
        updateAttempt.Parameters.AddWithValue("provider_receipt", (object?)providerResult.ProviderReceipt ?? DBNull.Value);
        updateAttempt.Parameters.AddWithValue("completed_at", now);
        updateAttempt.Parameters.AddWithValue("attempt_id", attemptId.Value);
        await updateAttempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await UpdateObligationAsync(connection, transaction, obligation, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(obligation);
    }

    public async Task<IReadOnlyList<NotificationProviderAttempt>> ReadAttemptsAsync(
        NotificationDeliveryId deliveryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT attempt_id, delivery_id, attempt_number, outcome, outcome_code,
                   provider_receipt, started_at, completed_at
            FROM {NotificationMigrations.Schema}.provider_attempt
            WHERE delivery_id = @delivery_id ORDER BY attempt_number;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("delivery_id", deliveryId.Value);
        var attempts = new List<NotificationProviderAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            attempts.Add(MapAttempt(reader));
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return attempts;
    }

    private static void AddObligationParameters(
        NpgsqlCommand command,
        NotificationDeliveryObligation obligation,
        string fingerprint)
    {
        command.Parameters.AddWithValue("delivery_id", obligation.DeliveryId.Value);
        command.Parameters.AddWithValue("event_id", obligation.EventId.Value);
        command.Parameters.AddWithValue("recipient_id", obligation.RecipientId.Value);
        command.Parameters.AddWithValue("covered_person_id", (object?)obligation.CoveredPersonId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("mandatory", obligation.Mandatory);
        command.Parameters.AddWithValue("endpoint", obligation.EmailAddress);
        command.Parameters.AddWithValue("subject", obligation.Subject);
        command.Parameters.AddWithValue("body", obligation.Body);
        command.Parameters.AddWithValue("next_attempt_at", obligation.NextAttemptAt);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        command.Parameters.AddWithValue("created_at", obligation.CreatedAt);
    }

    private static async Task<(NotificationDeliveryObligation Obligation, string Fingerprint)?> ReadByEventRecipientAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperationalEventId eventId,
        PersonId recipientId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT delivery_id, event_id, recipient_id, covered_person_id, mandatory, endpoint, subject, body,
                   status, attempt_count, next_attempt_at, active_attempt_id, lease_owner, lease_until,
                   version, created_at, completed_at, fingerprint
            FROM {NotificationMigrations.Schema}.delivery_obligation
            WHERE event_id = @event_id AND recipient_id = @recipient_id AND channel = 2;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("event_id", eventId.Value);
        command.Parameters.AddWithValue("recipient_id", recipientId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (MapObligation(reader), reader.GetString(17))
            : null;
    }

    private static async Task<NotificationDeliveryObligation?> ReadByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NotificationDeliveryId deliveryId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT delivery_id, event_id, recipient_id, covered_person_id, mandatory, endpoint, subject, body,
                   status, attempt_count, next_attempt_at, active_attempt_id, lease_owner, lease_until,
                   version, created_at, completed_at
            FROM {NotificationMigrations.Schema}.delivery_obligation
            WHERE delivery_id = @delivery_id{(forUpdate ? " FOR UPDATE" : string.Empty)};
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("delivery_id", deliveryId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapObligation(reader) : null;
    }

    private static async Task<NotificationProviderAttempt?> ReadAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NotificationAttemptId attemptId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT attempt_id, delivery_id, attempt_number, outcome, outcome_code,
                   provider_receipt, started_at, completed_at
            FROM {NotificationMigrations.Schema}.provider_attempt
            WHERE attempt_id = @attempt_id{(forUpdate ? " FOR UPDATE" : string.Empty)};
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("attempt_id", attemptId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapAttempt(reader) : null;
    }

    private static async Task UpdateObligationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NotificationDeliveryObligation obligation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {NotificationMigrations.Schema}.delivery_obligation
            SET status = @status, attempt_count = @attempt_count, next_attempt_at = @next_attempt_at,
                active_attempt_id = @active_attempt_id, lease_owner = @lease_owner, lease_until = @lease_until,
                version = @version, completed_at = @completed_at
            WHERE delivery_id = @delivery_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("status", (short)obligation.Status);
        command.Parameters.AddWithValue("attempt_count", obligation.AttemptCount);
        command.Parameters.AddWithValue("next_attempt_at", obligation.NextAttemptAt);
        command.Parameters.AddWithValue("active_attempt_id", (object?)obligation.ActiveAttemptId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("lease_owner", (object?)obligation.LeaseOwner ?? DBNull.Value);
        command.Parameters.AddWithValue("lease_until", (object?)obligation.LeaseUntil ?? DBNull.Value);
        command.Parameters.AddWithValue("version", checked((long)obligation.Version.Value));
        command.Parameters.AddWithValue("completed_at", (object?)obligation.CompletedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("delivery_id", obligation.DeliveryId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NotificationDeliveryObligation MapObligation(NpgsqlDataReader reader) => new(
        NotificationDeliveryId.From(reader.GetGuid(0)),
        OperationalEventId.From(reader.GetGuid(1)),
        PersonId.From(reader.GetGuid(2)),
        reader.IsDBNull(3) ? null : PersonId.From(reader.GetGuid(3)),
        reader.GetBoolean(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        (NotificationDeliveryStatus)reader.GetInt16(8),
        reader.GetInt32(9),
        reader.GetFieldValue<DateTimeOffset>(10),
        reader.IsDBNull(11) ? null : NotificationAttemptId.From(reader.GetGuid(11)),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
        StateVersion.From(checked((ulong)reader.GetInt64(14))),
        reader.GetFieldValue<DateTimeOffset>(15),
        reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16));

    private static NotificationProviderAttempt MapAttempt(NpgsqlDataReader reader) => new(
        NotificationAttemptId.From(reader.GetGuid(0)),
        NotificationDeliveryId.From(reader.GetGuid(1)),
        reader.GetInt32(2),
        (NotificationAttemptOutcome)reader.GetInt16(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));

    private async Task SetRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE \"{databaseRole}\";", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Fingerprint(NotificationDeliveryObligation obligation) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            EventId = obligation.EventId.Value,
            RecipientId = obligation.RecipientId.Value,
            CoveredPersonId = obligation.CoveredPersonId?.Value,
            obligation.Mandatory,
            obligation.EmailAddress,
            obligation.Subject,
            obligation.Body,
        }))));

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));
}

public sealed class NotificationDeliveryWorker
{
    private readonly NotificationDeliveryStore store;
    private readonly SmtpNotificationProvider provider;
    private readonly NotificationDeliveryPolicy policy;

    public NotificationDeliveryWorker(
        NotificationDeliveryStore store,
        SmtpNotificationProvider provider,
        NotificationDeliveryPolicy policy)
    {
        this.store = store;
        this.provider = provider;
        this.policy = policy;
    }

    public async Task<Result<NotificationDeliveryRunResult>> RunOnceAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var claim = await store.ClaimAsync(workerId, leaseDuration, cancellationToken).ConfigureAwait(false);
        if (claim.IsFailure)
        {
            return Result.Failure<NotificationDeliveryRunResult>(claim.Error!);
        }

        if (claim.Value.Claim is null)
        {
            return Result.Success(new NotificationDeliveryRunResult(null));
        }

        var value = claim.Value.Claim;
        var outcome = await provider.SendAsync(
            new SmtpDeliveryMessage(
                value.Attempt.AttemptId,
                value.Obligation.EmailAddress,
                value.Obligation.Subject,
                value.Obligation.Body),
            cancellationToken).ConfigureAwait(false);
        var completed = await store.CompleteAsync(
            workerId,
            value.Attempt.AttemptId,
            outcome,
            policy,
            cancellationToken).ConfigureAwait(false);
        return completed.IsSuccess
            ? Result.Success(new NotificationDeliveryRunResult(completed.Value))
            : Result.Failure<NotificationDeliveryRunResult>(completed.Error!);
    }
}
