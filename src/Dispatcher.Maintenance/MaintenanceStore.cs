using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;
using NpgsqlTypes;

namespace Dispatcher.Maintenance;

public sealed class MaintenanceStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;

    public MaintenanceStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        if (databaseRole.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new ArgumentException("Invalid PostgreSQL role name.", nameof(databaseRole));
        }

        this.databaseRole = databaseRole;
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<Result<MaintenanceCommandResult>> CreateAsync(
        AuthorizedMutation authorization,
        CreateMaintenanceAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateCommand(
            authorization, MaintenancePermissions.ManageAsset(request.ScopeId),
            request.Code, request.Name, request.IdempotencyKey);
        if (validation.IsFailure)
        {
            return Result.Failure<MaintenanceCommandResult>(validation.Error!);
        }

        var now = UtcNow();
        var dto = new AssetDto(
            request.AssetId.Value, request.ScopeId.Value, request.Code.Trim(), request.Name.Trim(), null,
            StateVersion.Initial.Value, now, now);
        var fingerprint = Fingerprint(JsonSerializer.Serialize(dto));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var receipt = await ReadReceiptAsync(
            connection, transaction, request.IdempotencyKey, "create", fingerprint, cancellationToken).ConfigureAwait(false);
        if (receipt.IsFailure)
        {
            return Result.Failure<MaintenanceCommandResult>(receipt.Error!);
        }

        if (receipt.Value.Value is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(new MaintenanceCommandResult(
                receipt.Value.Value.ToModel(), MaintenanceCommandDisposition.Replay));
        }

        await using (var command = new NpgsqlCommand(
            $"""
            INSERT INTO {MaintenanceMigrations.Schema}.asset
                (asset_id, scope_id, code, name, equipment_id, version, created_at, updated_at)
            VALUES (@asset, @scope, @code, @name, NULL, 1, @now, @now);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("asset", dto.AssetId);
            command.Parameters.AddWithValue("scope", dto.ScopeId);
            command.Parameters.AddWithValue("code", dto.Code);
            command.Parameters.AddWithValue("name", dto.Name);
            command.Parameters.AddWithValue("now", now);
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Failure<MaintenanceCommandResult>(
                    "maintenance.asset_conflict", "Maintenance asset identity or scope code already exists.");
            }
        }

        await WriteReceiptAndAuditAsync(
            connection, transaction, request.IdempotencyKey, "create", fingerprint, dto,
            authorization, StateVersion.Initial, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new MaintenanceCommandResult(dto.ToModel(), MaintenanceCommandDisposition.Applied));
    }

    public Task<Result<MaintenanceCommandResult>> UpdateAsync(
        AuthorizedMutation authorization, UpdateMaintenanceAssetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return MutateAsync(
            authorization, request.AssetId, request.ExpectedVersion, request.IdempotencyKey, "update",
            JsonSerializer.Serialize(new
            {
                AssetId = request.AssetId.Value,
                Code = request.Code.Trim(),
                Name = request.Name.Trim(),
                ExpectedVersion = request.ExpectedVersion.Value,
            }),
            current =>
            {
                var validation = ValidateText(request.Code, request.Name);
                return validation.IsFailure
                    ? Result.Failure<Mutation>(validation.Error!)
                    : Result.Success(new Mutation(
                        current with { Code = request.Code.Trim(), Name = request.Name.Trim() }, null));
            },
            cancellationToken);
    }

    public Task<Result<MaintenanceCommandResult>> LinkEquipmentAsync(
        AuthorizedMutation authorization, LinkMaintenanceEquipmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return MutateAsync(
            authorization, request.AssetId, request.ExpectedVersion, request.IdempotencyKey, "link-equipment",
            JsonSerializer.Serialize(new
            {
                AssetId = request.AssetId.Value,
                EquipmentId = request.EquipmentId.Value,
                ExpectedVersion = request.ExpectedVersion.Value,
            }),
            current => current.EquipmentId == request.EquipmentId.Value
                ? Failure<Mutation>("maintenance.equipment_link", "Equipment is already linked to this asset.")
                : Result.Success(new Mutation(
                    current with { EquipmentId = request.EquipmentId.Value },
                    MaintenanceEquipmentLinkAction.Linked)),
            cancellationToken);
    }

    public Task<Result<MaintenanceCommandResult>> UnlinkEquipmentAsync(
        AuthorizedMutation authorization, UnlinkMaintenanceEquipmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return MutateAsync(
            authorization, request.AssetId, request.ExpectedVersion, request.IdempotencyKey, "unlink-equipment",
            JsonSerializer.Serialize(new
            {
                AssetId = request.AssetId.Value,
                ExpectedVersion = request.ExpectedVersion.Value,
            }),
            current => current.EquipmentId is null
                ? Failure<Mutation>("maintenance.equipment_link", "Maintenance asset has no equipment link.")
                : Result.Success(new Mutation(
                    current with { EquipmentId = null },
                    MaintenanceEquipmentLinkAction.Unlinked)),
            cancellationToken);
    }

    public async Task<MaintenanceAssetSnapshot?> ReadAssetAsync(
        MaintenanceAssetId assetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var dto = await ReadAssetDtoAsync(connection, transaction, assetId, false, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return dto?.ToModel();
    }

    public async Task<IReadOnlyList<MaintenanceAssetSnapshot>> ReadAssetsAsync(
        FacilityScopeId scopeId, CancellationToken cancellationToken = default)
    {
        var results = new List<MaintenanceAssetSnapshot>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT asset_id, scope_id, code, name, equipment_id, version, created_at, updated_at
            FROM {MaintenanceMigrations.Schema}.asset WHERE scope_id = @scope ORDER BY code, asset_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("scope", scopeId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadAsset(reader).ToModel());
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<IReadOnlyList<MaintenanceEquipmentLinkHistory>> ReadLinkHistoryAsync(
        MaintenanceAssetId assetId, CancellationToken cancellationToken = default)
    {
        var results = new List<MaintenanceEquipmentLinkHistory>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT asset_version, action, equipment_id, changed_at
            FROM {MaintenanceMigrations.Schema}.equipment_link_history
            WHERE asset_id = @asset ORDER BY asset_version;
            """, connection, transaction);
        command.Parameters.AddWithValue("asset", assetId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new MaintenanceEquipmentLinkHistory(
                assetId,
                StateVersion.From(checked((ulong)reader.GetInt64(0))),
                (MaintenanceEquipmentLinkAction)reader.GetInt16(1),
                reader.IsDBNull(2) ? null : EquipmentId.From(reader.GetGuid(2)),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async Task<long> CountAuditAsync(
        MaintenanceAssetId assetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {MaintenanceMigrations.Schema}.mutation_audit WHERE asset_id = @asset;",
            connection, transaction);
        command.Parameters.AddWithValue("asset", assetId.Value);
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    private async Task<Result<MaintenanceCommandResult>> MutateAsync(
        AuthorizedMutation authorization,
        MaintenanceAssetId assetId,
        StateVersion expectedVersion,
        string idempotencyKey,
        string action,
        string requestContent,
        Func<AssetDto, Result<Mutation>> transform,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (!expectedVersion.IsDefined || idempotencyKey.Length > 200)
        {
            return Failure<MaintenanceCommandResult>("maintenance.command_invalid", "Maintenance command is invalid.");
        }

        var fingerprint = Fingerprint(requestContent);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var receipt = await ReadReceiptAsync(
            connection, transaction, idempotencyKey, action, fingerprint, cancellationToken).ConfigureAwait(false);
        if (receipt.IsFailure)
        {
            return Result.Failure<MaintenanceCommandResult>(receipt.Error!);
        }

        if (receipt.Value.Value is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(new MaintenanceCommandResult(
                receipt.Value.Value.ToModel(), MaintenanceCommandDisposition.Replay));
        }

        var current = await ReadAssetDtoAsync(connection, transaction, assetId, true, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Failure<MaintenanceCommandResult>("maintenance.asset_not_found", "Maintenance asset was not found.");
        }

        if (authorization.Permission != MaintenancePermissions.ManageAsset(FacilityScopeId.From(current.ScopeId)))
        {
            return Failure<MaintenanceCommandResult>(
                "maintenance.permission_contract", "Authorization does not match the asset scope.");
        }

        if (current.Version != expectedVersion.Value)
        {
            return Failure<MaintenanceCommandResult>("maintenance.asset_version", "Maintenance asset version is stale.");
        }

        var transformed = transform(current);
        if (transformed.IsFailure)
        {
            return Result.Failure<MaintenanceCommandResult>(transformed.Error!);
        }

        var now = UtcNow();
        var next = transformed.Value.Asset with { Version = expectedVersion.Next().Value, UpdatedAt = now };
        await using (var command = new NpgsqlCommand(
            $"""
            UPDATE {MaintenanceMigrations.Schema}.asset
            SET code = @code, name = @name, equipment_id = @equipment, version = @version, updated_at = @updated
            WHERE asset_id = @asset;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("asset", next.AssetId);
            command.Parameters.AddWithValue("code", next.Code);
            command.Parameters.AddWithValue("name", next.Name);
            command.Parameters.Add(new NpgsqlParameter("equipment", NpgsqlDbType.Uuid)
            {
                Value = next.EquipmentId is null ? DBNull.Value : next.EquipmentId.Value,
            });
            command.Parameters.AddWithValue("version", checked((long)next.Version));
            command.Parameters.AddWithValue("updated", next.UpdatedAt);
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Failure<MaintenanceCommandResult>(
                    "maintenance.asset_conflict", "Maintenance asset scope code already exists.");
            }
        }

        if (transformed.Value.LinkAction is not null)
        {
            await WriteLinkHistoryAsync(
                connection, transaction, next, transformed.Value.LinkAction.Value, now, cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteReceiptAndAuditAsync(
            connection, transaction, idempotencyKey, action, fingerprint, next,
            authorization, StateVersion.From(next.Version), now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new MaintenanceCommandResult(next.ToModel(), MaintenanceCommandDisposition.Applied));
    }

    private static Result ValidateCommand(
        AuthorizedMutation authorization, PermissionCode requiredPermission,
        string code, string name, string idempotencyKey)
    {
        if (authorization.Permission != requiredPermission)
        {
            return Failure("maintenance.permission_contract", "Authorization does not match the asset scope.");
        }

        var text = ValidateText(code, name);
        return text.IsFailure || string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200
            ? Failure("maintenance.command_invalid", "Maintenance command is invalid.")
            : Result.Success();
    }

    private static Result ValidateText(string code, string name) =>
        string.IsNullOrWhiteSpace(code) || code.Trim().Length > 100 ||
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > 500
            ? Failure("maintenance.asset_invalid", "Maintenance asset code or name is invalid.")
            : Result.Success();

    private static async Task<Result<Receipt>> ReadReceiptAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string key, string action, string fingerprint, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT action, fingerprint, result_snapshot::text FROM {MaintenanceMigrations.Schema}.command_receipt WHERE idempotency_key = @key;",
            connection, transaction);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Result.Success(new Receipt(null));
        }

        if (!string.Equals(reader.GetString(0), action, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), fingerprint, StringComparison.Ordinal))
        {
            return Failure<Receipt>("maintenance.idempotency_conflict", "Idempotency key was used for another command.");
        }

        return Result.Success(new Receipt(JsonSerializer.Deserialize<AssetDto>(reader.GetString(2))!));
    }

    private static async Task<AssetDto?> ReadAssetDtoAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        MaintenanceAssetId assetId, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT asset_id, scope_id, code, name, equipment_id, version, created_at, updated_at
            FROM {MaintenanceMigrations.Schema}.asset WHERE asset_id = @asset{(forUpdate ? " FOR UPDATE" : string.Empty)};
            """, connection, transaction);
        command.Parameters.AddWithValue("asset", assetId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAsset(reader) : null;
    }

    private static AssetDto ReadAsset(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetGuid(4), checked((ulong)reader.GetInt64(5)),
        reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7));

    private static async Task WriteLinkHistoryAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        AssetDto asset, MaintenanceEquipmentLinkAction action, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {MaintenanceMigrations.Schema}.equipment_link_history
                (asset_id, asset_version, action, equipment_id, changed_at)
            VALUES (@asset, @version, @action, @equipment, @changed);
            """, connection, transaction);
        command.Parameters.AddWithValue("asset", asset.AssetId);
        command.Parameters.AddWithValue("version", checked((long)asset.Version));
        command.Parameters.AddWithValue("action", checked((short)action));
        command.Parameters.Add(new NpgsqlParameter("equipment", NpgsqlDbType.Uuid)
        {
            Value = asset.EquipmentId is null ? DBNull.Value : asset.EquipmentId.Value,
        });
        command.Parameters.AddWithValue("changed", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteReceiptAndAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string key, string action, string fingerprint, AssetDto result,
        AuthorizedMutation authorization, StateVersion resultingVersion, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand(
            $"""
            INSERT INTO {MaintenanceMigrations.Schema}.command_receipt
                (idempotency_key, action, fingerprint, result_snapshot, applied_at)
            VALUES (@key, @action, @fingerprint, CAST(@result AS jsonb), @now);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("key", key);
            command.Parameters.AddWithValue("action", action);
            command.Parameters.AddWithValue("fingerprint", fingerprint);
            command.Parameters.AddWithValue("result", JsonSerializer.Serialize(result));
            command.Parameters.AddWithValue("now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var audit = new NpgsqlCommand(
            $"""
            INSERT INTO {MaintenanceMigrations.Schema}.mutation_audit
                (audit_id, asset_id, scope_id, session_id, subject_id, action, resulting_version, changed_at)
            VALUES (@audit, @asset, @scope, @session, @subject, @action, @version, @now);
            """, connection, transaction);
        audit.Parameters.AddWithValue("audit", Guid.NewGuid());
        audit.Parameters.AddWithValue("asset", result.AssetId);
        audit.Parameters.AddWithValue("scope", result.ScopeId);
        audit.Parameters.AddWithValue("session", authorization.Session.Id.Value);
        audit.Parameters.AddWithValue("subject", authorization.Session.SubjectId.Value);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("version", checked((long)resultingVersion.Value));
        audit.Parameters.AddWithValue("now", now);
        await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SetRoleAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE {databaseRole};", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private DateTimeOffset UtcNow()
    {
        var now = clock.GetUtcNow();
        return now.Offset == TimeSpan.Zero
            ? now
            : throw new InvalidOperationException("Maintenance persistence requires a UTC wall clock.");
    }

    private static string Fingerprint(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));

    private sealed record Mutation(AssetDto Asset, MaintenanceEquipmentLinkAction? LinkAction);
    private sealed record Receipt(AssetDto? Value);

    private sealed record AssetDto(
        Guid AssetId, Guid ScopeId, string Code, string Name, Guid? EquipmentId,
        ulong Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
    {
        public MaintenanceAssetSnapshot ToModel() => new(
            MaintenanceAssetId.From(AssetId), FacilityScopeId.From(ScopeId), Code, Name,
            EquipmentId is null ? null : Dispatcher.Equipment.EquipmentId.From(EquipmentId.Value),
            StateVersion.From(Version), CreatedAt, UpdatedAt);
    }
}
