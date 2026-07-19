using System.Security.Cryptography;
using Dispatcher.Semantics;
using Npgsql;

namespace Dispatcher.Platform;

public readonly record struct OperationId
{
    private readonly CanonicalId<OperationId> value;

    private OperationId(CanonicalId<OperationId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static OperationId New() => new(CanonicalId.New<OperationId>());

    public static OperationId From(Guid value) => new(CanonicalId.From<OperationId>(value));
}

public sealed record RequestFingerprint
{
    private RequestFingerprint(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static RequestFingerprint Compute(ReadOnlySpan<byte> request) =>
        new(Convert.ToHexString(SHA256.HashData(request)));
}

public sealed record OperationAdmissionRequest
{
    public OperationAdmissionRequest(
        OperationId operationId,
        string idempotencyKey,
        RequestFingerprint fingerprint,
        string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        if (idempotencyKey.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(idempotencyKey), "Idempotency key is limited to 200 characters.");
        }

        _ = operationId.Value;
        OperationId = operationId;
        IdempotencyKey = idempotencyKey;
        Fingerprint = fingerprint;
        Action = action;
    }

    public OperationId OperationId { get; }

    public string IdempotencyKey { get; }

    public RequestFingerprint Fingerprint { get; }

    public string Action { get; }
}

public enum AdmissionDisposition
{
    Accepted = 1,
    Replay = 2,
    Conflict = 3,
}

public sealed record OperationAdmissionResult(OperationId OperationId, AdmissionDisposition Disposition);

public sealed class OperationAdmissionStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly PlatformDatabaseRole databaseRole;
    private readonly IWallClock clock;

    public OperationAdmissionStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(clock);
        this.dataSource = dataSource;
        this.databaseRole = PlatformDatabaseRole.From(databaseRole);
        this.clock = clock;
    }

    public async Task<OperationAdmissionResult> AdmitAsync(
        AuthorizedMutation authorization,
        OperationAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);

        using var activity = PlatformDiagnostics.ActivitySource.StartActivity("operation.admit");
        activity?.SetTag("operation.action", request.Action);
        activity?.SetTag("principal.kind", authorization.Session.PrincipalKind.ToString());

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await databaseRole.SetLocalAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        var admittedAt = clock.GetUtcNow();
        var inserted = await TryInsertOperationAsync(
            connection,
            transaction,
            authorization,
            request,
            admittedAt,
            cancellationToken).ConfigureAwait(false);

        if (inserted)
        {
            await InsertAuditAsync(
                connection,
                transaction,
                authorization,
                request,
                admittedAt,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            PlatformDiagnostics.Admissions.Add(1);
            return new OperationAdmissionResult(request.OperationId, AdmissionDisposition.Accepted);
        }

        var existing = await ReadExistingAsync(
            connection,
            transaction,
            authorization.Session.SubjectId,
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (existing is not null &&
            string.Equals(existing.Fingerprint, request.Fingerprint.Value, StringComparison.Ordinal) &&
            string.Equals(existing.Action, request.Action, StringComparison.Ordinal))
        {
            PlatformDiagnostics.AdmissionReplays.Add(1);
            return new OperationAdmissionResult(OperationId.From(existing.OperationId), AdmissionDisposition.Replay);
        }

        PlatformDiagnostics.AdmissionConflicts.Add(1);
        return new OperationAdmissionResult(request.OperationId, AdmissionDisposition.Conflict);
    }

    private static async Task<bool> TryInsertOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuthorizedMutation authorization,
        OperationAdmissionRequest request,
        DateTimeOffset admittedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {PlatformMigrations.Schema}.operation_admission
                (operation_id, session_id, subject_id, idempotency_key, request_fingerprint, action, admitted_at)
            VALUES
                (@operation_id, @session_id, @subject_id, @idempotency_key, @fingerprint, @action, @admitted_at)
            ON CONFLICT DO NOTHING
            RETURNING operation_id;
            """,
            connection,
            transaction);
        AddOperationParameters(command, authorization, request, admittedAt);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid;
    }

    private static async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuthorizedMutation authorization,
        OperationAdmissionRequest request,
        DateTimeOffset admittedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {PlatformMigrations.Schema}.audit_admission
                (audit_id, operation_id, session_id, subject_id, principal_kind, permission, action, admitted_at)
            VALUES
                (@audit_id, @operation_id, @session_id, @subject_id, @principal_kind, @permission, @action, @admitted_at);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("audit_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("operation_id", request.OperationId.Value);
        command.Parameters.AddWithValue("session_id", authorization.Session.Id.Value);
        command.Parameters.AddWithValue("subject_id", authorization.Session.SubjectId.Value);
        command.Parameters.AddWithValue("principal_kind", (short)authorization.Session.PrincipalKind);
        command.Parameters.AddWithValue("permission", authorization.Permission.Value);
        command.Parameters.AddWithValue("action", request.Action);
        command.Parameters.AddWithValue("admitted_at", admittedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ExistingAdmission?> ReadExistingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SubjectId subjectId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT operation_id, request_fingerprint, action
            FROM {PlatformMigrations.Schema}.operation_admission
            WHERE subject_id = @subject_id AND idempotency_key = @idempotency_key;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("subject_id", subjectId.Value);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ExistingAdmission(reader.GetGuid(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static void AddOperationParameters(
        NpgsqlCommand command,
        AuthorizedMutation authorization,
        OperationAdmissionRequest request,
        DateTimeOffset admittedAt)
    {
        command.Parameters.AddWithValue("operation_id", request.OperationId.Value);
        command.Parameters.AddWithValue("session_id", authorization.Session.Id.Value);
        command.Parameters.AddWithValue("subject_id", authorization.Session.SubjectId.Value);
        command.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
        command.Parameters.AddWithValue("fingerprint", request.Fingerprint.Value);
        command.Parameters.AddWithValue("action", request.Action);
        command.Parameters.AddWithValue("admitted_at", admittedAt);
    }

    private sealed record ExistingAdmission(Guid OperationId, string Fingerprint, string Action);
}
