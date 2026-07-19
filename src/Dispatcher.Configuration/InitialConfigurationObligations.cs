using System.Text.Json;
using System.Text.RegularExpressions;
using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;
using NpgsqlTypes;

namespace Dispatcher.Configuration;

public sealed record AcceptInitialConfiguration(
    Guid ObligationId,
    string RequestFingerprint,
    FacilityScopeId ScopeId,
    EquipmentId EquipmentId,
    EquipmentProtocol Protocol,
    string FormDataJson,
    byte[]? ProtectedSecret);

public sealed record InitialConfigurationObligationSnapshot(
    Guid ObligationId,
    FacilityScopeId ScopeId,
    EquipmentId EquipmentId,
    EquipmentProtocol Protocol,
    bool HasSecret,
    DateTimeOffset AcceptedAt);

public sealed class InitialConfigurationService
{
    private readonly InitialConfigurationStore store;
    private readonly IWallClock clock;

    public InitialConfigurationService(InitialConfigurationStore store, IWallClock clock)
    {
        this.store = store;
        this.clock = clock;
    }

    public async Task<Result<InitialConfigurationObligationSnapshot>> AcceptAsync(
        SessionSnapshot? session,
        AcceptInitialConfiguration request,
        CancellationToken cancellationToken = default)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(
            session,
            ConfigurationPermissions.Save(request.ScopeId),
            clock);
        return authorization.IsFailure
            ? Result.Failure<InitialConfigurationObligationSnapshot>(authorization.Error!)
            : await store.AcceptAsync(authorization.Value, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<InitialConfigurationObligationSnapshot>> ReadAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        Guid obligationId,
        CancellationToken cancellationToken = default)
    {
        var authorization = SessionAuthorization.AuthorizeAccess(
            session,
            ConfigurationPermissions.Read(scopeId),
            clock);
        return authorization.IsFailure
            ? Result.Failure<InitialConfigurationObligationSnapshot>(authorization.Error!)
            : await store.ReadAsync(scopeId, obligationId, cancellationToken).ConfigureAwait(false);
    }
}

public sealed partial class InitialConfigurationStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;

    public InitialConfigurationStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        if (!RolePattern().IsMatch(databaseRole))
        {
            throw new ArgumentException("Invalid PostgreSQL role name.", nameof(databaseRole));
        }

        this.dataSource = dataSource;
        this.databaseRole = databaseRole;
        this.clock = clock;
    }

    public async Task<Result<InitialConfigurationObligationSnapshot>> AcceptAsync(
        AuthorizedAccess authorization,
        AcceptInitialConfiguration request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ObligationId == Guid.Empty || string.IsNullOrWhiteSpace(request.RequestFingerprint))
        {
            return Failure<InitialConfigurationObligationSnapshot>(
                "configuration.obligation_invalid",
                "Obligation identity and fingerprint are required.");
        }

        string formJson;
        try
        {
            using var form = JsonDocument.Parse(request.FormDataJson);
            formJson = JsonSerializer.Serialize(form.RootElement);
        }
        catch (JsonException exception)
        {
            return Failure<InitialConfigurationObligationSnapshot>("configuration.obligation_invalid", exception.Message);
        }

        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand(
                         $"""
                         INSERT INTO {ConfigurationMigrations.Schema}.initial_configuration_obligation
                             (obligation_id, request_fingerprint, scope_id, equipment_id,
                              protocol, form_data, protected_secret, accepted_at)
                         VALUES
                             (@obligation_id, @fingerprint, @scope_id, @equipment_id,
                              @protocol, @form_data, @protected_secret, @accepted_at)
                         ON CONFLICT DO NOTHING;
                         """,
                         connection,
                         transaction))
        {
            insert.Parameters.AddWithValue("obligation_id", request.ObligationId);
            insert.Parameters.AddWithValue("fingerprint", request.RequestFingerprint);
            insert.Parameters.AddWithValue("scope_id", request.ScopeId.Value);
            insert.Parameters.AddWithValue("equipment_id", request.EquipmentId.Value);
            insert.Parameters.AddWithValue(
                "protocol",
                request.Protocol == EquipmentProtocol.ModbusTcp ? "modbus_tcp" : "snmp");
            insert.Parameters.AddWithValue("form_data", NpgsqlDbType.Jsonb, formJson);
            insert.Parameters.AddWithValue("protected_secret", (object?)request.ProtectedSecret ?? DBNull.Value);
            insert.Parameters.AddWithValue("accepted_at", now);
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            var existing = await ReadRecordAsync(
                connection,
                transaction,
                request.ObligationId,
                cancellationToken).ConfigureAwait(false);
            if (existing is null || existing.Value.Fingerprint != request.RequestFingerprint ||
                existing.Value.Snapshot.ScopeId != request.ScopeId ||
                existing.Value.Snapshot.EquipmentId != request.EquipmentId)
            {
                return Failure<InitialConfigurationObligationSnapshot>(
                    "configuration.obligation_conflict",
                    "Obligation ID was already accepted for a different request.");
            }

            if (inserted)
            {
                await InsertAuditAsync(
                    connection,
                    transaction,
                    authorization,
                    existing.Value.Snapshot,
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(existing.Value.Snapshot);
        }
    }

    public async Task<Result<InitialConfigurationObligationSnapshot>> ReadAsync(
        FacilityScopeId scopeId,
        Guid obligationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var record = await ReadRecordAsync(connection, transaction, obligationId, cancellationToken).ConfigureAwait(false);
        if (record is null || record.Value.Snapshot.ScopeId != scopeId)
        {
            return Failure<InitialConfigurationObligationSnapshot>(
                "configuration.obligation_not_found",
                "Initial configuration obligation was not found.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(record.Value.Snapshot);
    }

    private static async Task<(InitialConfigurationObligationSnapshot Snapshot, string Fingerprint)?> ReadRecordAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid obligationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT obligation_id, request_fingerprint, scope_id, equipment_id,
                   protocol, protected_secret IS NOT NULL, accepted_at
            FROM {ConfigurationMigrations.Schema}.initial_configuration_obligation
            WHERE obligation_id = @obligation_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("obligation_id", obligationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (new InitialConfigurationObligationSnapshot(
            reader.GetGuid(0),
            FacilityScopeId.From(reader.GetGuid(2)),
            EquipmentId.From(reader.GetGuid(3)),
            reader.GetString(4) == "modbus_tcp" ? EquipmentProtocol.ModbusTcp : EquipmentProtocol.Snmp,
            reader.GetBoolean(5),
            reader.GetFieldValue<DateTimeOffset>(6)), reader.GetString(1));
    }

    private async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuthorizedAccess authorization,
        InitialConfigurationObligationSnapshot obligation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {ConfigurationMigrations.Schema}.obligation_audit
                (audit_id, obligation_id, scope_id, equipment_id, session_id,
                 subject_id, permission, action, changed_at)
            VALUES
                (@audit_id, @obligation_id, @scope_id, @equipment_id, @session_id,
                 @subject_id, @permission, 'initial_configuration.accept', @changed_at);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("audit_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("obligation_id", obligation.ObligationId);
        command.Parameters.AddWithValue("scope_id", obligation.ScopeId.Value);
        command.Parameters.AddWithValue("equipment_id", obligation.EquipmentId.Value);
        command.Parameters.AddWithValue("session_id", authorization.Session.Id.Value);
        command.Parameters.AddWithValue("subject_id", authorization.Session.SubjectId.Value);
        command.Parameters.AddWithValue("permission", authorization.Permission.Value);
        command.Parameters.AddWithValue("changed_at", clock.GetUtcNow());
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

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex RolePattern();
}
