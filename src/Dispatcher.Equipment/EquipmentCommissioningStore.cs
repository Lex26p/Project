using System.Text.Json;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;
using NpgsqlTypes;

namespace Dispatcher.Equipment;

public sealed record EquipmentStagingApplyWorkItem(
    EquipmentStagingDraftSnapshot Draft,
    StagingRowInput Row);

public sealed partial class EquipmentStagingStore
{
    public async Task<Result<EquipmentStagingDraftSnapshot>> SaveDraftAsync(
        AuthorizedAccess authorization,
        EquipmentStagingDraftInput input,
        StagingSecretProtector protector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(protector);
        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        var existing = await ReadDraftRecordAsync(
            connection, transaction, input.ScopeId, input.RowId, true, cancellationToken).ConfigureAwait(false);
        if (existing is null && input.ExpectedVersion is not null ||
            existing is not null && input.ExpectedVersion != existing.Value.Snapshot.Version)
        {
            return Failure<EquipmentStagingDraftSnapshot>(
                "staging.version_conflict",
                "Staging row changed concurrently.");
        }

        var secretReference = existing?.SecretReference;
        if (input.Secret is not null)
        {
            var secretId = Guid.CreateVersion7();
            secretReference = $"db:{secretId:N}";
            await using var secret = new NpgsqlCommand(
                $"""
                INSERT INTO {EquipmentMigrations.Schema}.protocol_secret
                    (secret_id, scope_id, protected_value, created_at)
                VALUES (@secret_id, @scope_id, @protected_value, @created_at);
                """,
                connection,
                transaction);
            secret.Parameters.AddWithValue("secret_id", secretId);
            secret.Parameters.AddWithValue("scope_id", input.ScopeId.Value);
            secret.Parameters.AddWithValue("protected_value", protector.Protect(input.Secret));
            secret.Parameters.AddWithValue("created_at", now);
            await secret.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var formJson = SerializeForm(input);
        var fingerprint = EquipmentCommissioningTools.ComputeFingerprint(input, secretReference);
        var version = (existing?.Snapshot.Version ?? 0) + 1;
        await using (var command = new NpgsqlCommand(
                         $"""
                         INSERT INTO {EquipmentMigrations.Schema}.staging_draft
                             (row_id, scope_id, equipment_id, location_id, code, name, protocol,
                              form_data, secret_reference, apply_action, update_authorized,
                              fingerprint, version, applied_at, created_at, updated_at)
                         VALUES
                             (@row_id, @scope_id, @equipment_id, @location_id, @code, @name, @protocol,
                              @form_data, @secret_reference, @apply_action, false,
                              @fingerprint, @version, NULL, @now, @now)
                         ON CONFLICT (row_id) DO UPDATE SET
                             equipment_id = EXCLUDED.equipment_id,
                             location_id = EXCLUDED.location_id,
                             code = EXCLUDED.code,
                             name = EXCLUDED.name,
                             protocol = EXCLUDED.protocol,
                             form_data = EXCLUDED.form_data,
                             secret_reference = EXCLUDED.secret_reference,
                             apply_action = EXCLUDED.apply_action,
                             update_authorized = CASE
                                 WHEN staging_draft.fingerprint = EXCLUDED.fingerprint
                                 THEN staging_draft.update_authorized
                                 ELSE false
                             END,
                             fingerprint = EXCLUDED.fingerprint,
                             version = EXCLUDED.version,
                             applied_at = CASE
                                 WHEN staging_draft.fingerprint = EXCLUDED.fingerprint
                                 THEN staging_draft.applied_at
                                 ELSE NULL
                             END,
                             updated_at = EXCLUDED.updated_at
                         WHERE staging_draft.scope_id = EXCLUDED.scope_id
                           AND staging_draft.version = @expected_version;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("row_id", input.RowId);
            command.Parameters.AddWithValue("scope_id", input.ScopeId.Value);
            command.Parameters.AddWithValue("equipment_id", input.EquipmentId.Value);
            command.Parameters.AddWithValue("location_id", input.LocationId.Value);
            command.Parameters.AddWithValue("code", input.Code.Trim());
            command.Parameters.AddWithValue("name", input.Name.Trim());
            command.Parameters.AddWithValue("protocol", Protocol(input.Protocol));
            command.Parameters.AddWithValue("form_data", NpgsqlDbType.Jsonb, formJson);
            command.Parameters.AddWithValue("secret_reference", (object?)secretReference ?? DBNull.Value);
            command.Parameters.AddWithValue("apply_action", (short)input.Action);
            command.Parameters.AddWithValue("fingerprint", fingerprint);
            command.Parameters.AddWithValue("version", version);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("expected_version", existing?.Snapshot.Version ?? 0);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return Failure<EquipmentStagingDraftSnapshot>(
                    "staging.version_conflict",
                    "Staging row changed concurrently.");
            }
        }

        var saved = await ReadDraftRecordAsync(
            connection, transaction, input.ScopeId, input.RowId, false, cancellationToken).ConfigureAwait(false);
        await InsertCommissioningAuditAsync(
            connection,
            transaction,
            authorization,
            input.ScopeId,
            input.RowId,
            null,
            existing is null ? "staging.draft_create" : "staging.draft_update",
            version,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(saved!.Value.Snapshot);
    }

    public async Task<Result<IReadOnlyList<EquipmentStagingDraftSnapshot>>> ReadDraftsAsync(
        FacilityScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT row_id
            FROM {EquipmentMigrations.Schema}.staging_draft
            WHERE scope_id = @scope_id
            ORDER BY updated_at, row_id
            LIMIT 1000;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        var rowIds = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rowIds.Add(reader.GetGuid(0));
            }
        }

        var rows = new List<EquipmentStagingDraftSnapshot>(rowIds.Count);
        foreach (var rowId in rowIds)
        {
            var row = await ReadDraftRecordAsync(
                connection, transaction, scopeId, rowId, false, cancellationToken).ConfigureAwait(false);
            if (row is not null)
            {
                rows.Add(row.Value.Snapshot);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<EquipmentStagingDraftSnapshot>>(rows);
    }

    public async Task<Result<EquipmentStagingDraftSnapshot>> ReadDraftAsync(
        FacilityScopeId scopeId,
        Guid rowId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var row = await ReadDraftRecordAsync(
            connection, transaction, scopeId, rowId, false, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return Failure<EquipmentStagingDraftSnapshot>("staging.row_not_found", "Staging row was not found.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(row.Value.Snapshot);
    }

    public async Task<Result<string>> BuildAppliedProtocolSourcesJsonAsync(
        FacilityScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT row_id
            FROM {EquipmentMigrations.Schema}.staging_draft
            WHERE scope_id = @scope_id AND applied_at IS NOT NULL
            ORDER BY row_id
            LIMIT 1000;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        var sources = new List<JsonElement>(ids.Count);
        foreach (var id in ids)
        {
            var row = await ReadDraftRecordAsync(
                connection, transaction, scopeId, id, false, cancellationToken).ConfigureAwait(false);
            if (row is null || row.Value.Snapshot.Action == StagingApplyAction.Skip)
            {
                continue;
            }

            using var manifest = JsonDocument.Parse(
                EquipmentCommissioningTools.BuildDiagnosticManifest(
                    row.Value.Snapshot, row.Value.SecretReference));
            sources.Add(manifest.RootElement.GetProperty("protocolSources")[0].Clone());
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(JsonSerializer.Serialize(sources));
    }

    public async Task<Result<EquipmentStagingApplyWorkItem>> PrepareApplyAsync(
        FacilityScopeId scopeId,
        Guid rowId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var record = await ReadDraftRecordAsync(
            connection, transaction, scopeId, rowId, false, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return Failure<EquipmentStagingApplyWorkItem>("staging.row_not_found", "Staging row was not found.");
        }

        var snapshot = record.Value.Snapshot;
        var form = new EquipmentProtocolForm(
            snapshot.Protocol,
            snapshot.Host,
            snapshot.Port,
            snapshot.ModbusUnitId,
            snapshot.SnmpVersion,
            record.Value.SecretReference is null
                ? null
                : WriteOnlySecret.From(record.Value.SecretReference));
        var row = new StagingRowInput(
            snapshot.RowId,
            snapshot.EquipmentId,
            snapshot.ScopeId,
            snapshot.LocationId,
            snapshot.Code,
            snapshot.Name,
            form);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new EquipmentStagingApplyWorkItem(snapshot, row));
    }

    public async Task<Result<EquipmentStagingDraftSnapshot>> AuthorizeUpdateAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        Guid rowId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var update = new NpgsqlCommand(
            $"""
            UPDATE {EquipmentMigrations.Schema}.staging_draft
            SET update_authorized = true, version = version + 1, updated_at = @now
            WHERE row_id = @row_id AND scope_id = @scope_id
              AND apply_action = 2 AND version = @expected_version;
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("now", clock.GetUtcNow());
        update.Parameters.AddWithValue("row_id", rowId);
        update.Parameters.AddWithValue("scope_id", scopeId.Value);
        update.Parameters.AddWithValue("expected_version", expectedVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            return Failure<EquipmentStagingDraftSnapshot>(
                "staging.version_conflict",
                "Staging update authorization could not be applied.");
        }

        var row = await ReadDraftRecordAsync(
            connection, transaction, scopeId, rowId, false, cancellationToken).ConfigureAwait(false);
        await InsertCommissioningAuditAsync(
            connection, transaction, authorization, scopeId, rowId, null,
            "staging.update_authorize", row!.Value.Snapshot.Version, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(row.Value.Snapshot);
    }

    public async Task<Result<EquipmentStagingDraftSnapshot>> MarkAppliedAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        Guid rowId,
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var update = new NpgsqlCommand(
            $"""
            UPDATE {EquipmentMigrations.Schema}.staging_draft
            SET applied_at = @now, version = version + 1, updated_at = @now
            WHERE row_id = @row_id AND scope_id = @scope_id AND fingerprint = @fingerprint;
            """,
            connection,
            transaction);
        update.Parameters.AddWithValue("now", clock.GetUtcNow());
        update.Parameters.AddWithValue("row_id", rowId);
        update.Parameters.AddWithValue("scope_id", scopeId.Value);
        update.Parameters.AddWithValue("fingerprint", fingerprint);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            return Failure<EquipmentStagingDraftSnapshot>(
                "staging.stale_fingerprint",
                "Staging row changed before apply completed.");
        }

        var row = await ReadDraftRecordAsync(
            connection, transaction, scopeId, rowId, false, cancellationToken).ConfigureAwait(false);
        await InsertCommissioningAuditAsync(
            connection, transaction, authorization, scopeId, rowId, null,
            "staging.apply", row!.Value.Snapshot.Version, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(row.Value.Snapshot);
    }

    public async Task<Result<EquipmentStagingTemplateSnapshot>> SaveTemplateAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        Guid rowId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(name));
        }

        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var draft = await ReadDraftRecordAsync(
            connection, transaction, scopeId, rowId, false, cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return Failure<EquipmentStagingTemplateSnapshot>(
                "staging.row_not_found",
                "Staging row was not found.");
        }

        var templateId = Guid.CreateVersion7();
        var formJson = JsonSerializer.Serialize(new
        {
            draft.Value.Snapshot.Port,
            draft.Value.Snapshot.ModbusTable,
            draft.Value.Snapshot.ModbusAddress,
            draft.Value.Snapshot.ModbusValueType,
            draft.Value.Snapshot.ModbusByteOrder,
            draft.Value.Snapshot.ModbusWordOrder,
            draft.Value.Snapshot.ModbusScale,
            draft.Value.Snapshot.SnmpVersion,
            draft.Value.Snapshot.SnmpOid,
            draft.Value.Snapshot.SnmpValueType,
            draft.Value.Snapshot.Unit,
        });
        await using (var command = new NpgsqlCommand(
                         $"""
                         INSERT INTO {EquipmentMigrations.Schema}.staging_template
                             (template_id, scope_id, name, protocol, form_data,
                              version, created_at, updated_at)
                         VALUES
                             (@template_id, @scope_id, @name, @protocol, @form_data,
                              1, @now, @now)
                         ON CONFLICT (scope_id, name) DO UPDATE SET
                             protocol = EXCLUDED.protocol,
                             form_data = EXCLUDED.form_data,
                             version = staging_template.version + 1,
                             updated_at = EXCLUDED.updated_at
                         RETURNING template_id;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("template_id", templateId);
            command.Parameters.AddWithValue("scope_id", scopeId.Value);
            command.Parameters.AddWithValue("name", name.Trim());
            command.Parameters.AddWithValue("protocol", Protocol(draft.Value.Snapshot.Protocol));
            command.Parameters.AddWithValue("form_data", NpgsqlDbType.Jsonb, formJson);
            command.Parameters.AddWithValue("now", now);
            templateId = (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Template identity was not returned."));
        }

        var template = await ReadTemplateAsync(
            connection, transaction, scopeId, templateId, cancellationToken).ConfigureAwait(false);
        await InsertCommissioningAuditAsync(
            connection, transaction, authorization, scopeId, rowId, null,
            "staging.template_save", template!.Version, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(template);
    }

    public async Task<Result<IReadOnlyList<EquipmentStagingTemplateSnapshot>>> ReadTemplatesAsync(
        FacilityScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT template_id
            FROM {EquipmentMigrations.Schema}.staging_template
            WHERE scope_id = @scope_id
            ORDER BY name
            LIMIT 500;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        var ids = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(reader.GetGuid(0));
            }
        }

        var templates = new List<EquipmentStagingTemplateSnapshot>(ids.Count);
        foreach (var id in ids)
        {
            templates.Add((await ReadTemplateAsync(
                connection, transaction, scopeId, id, cancellationToken).ConfigureAwait(false))!);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<EquipmentStagingTemplateSnapshot>>(templates);
    }

    public async Task<Result<EquipmentStagingTemplateSnapshot>> ReadTemplateAsync(
        FacilityScopeId scopeId,
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var template = await ReadTemplateAsync(
            connection, transaction, scopeId, templateId, cancellationToken).ConfigureAwait(false);
        if (template is null)
        {
            return Failure<EquipmentStagingTemplateSnapshot>(
                "staging.template_not_found",
                "Staging template was not found.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(template);
    }

    public async Task<Result> DeleteTemplateAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            DELETE FROM {EquipmentMigrations.Schema}.staging_template
            WHERE template_id = @template_id AND scope_id = @scope_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("template_id", templateId);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            return Failure("staging.template_not_found", "Staging template was not found.");
        }

        await InsertCommissioningAuditAsync(
            connection, transaction, authorization, scopeId, null, null,
            "staging.template_delete", null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<EquipmentDiagnosticJobSnapshot>> EnqueueDiagnosticAsync(
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        Guid rowId,
        EquipmentDiagnosticMode mode,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var draft = await ReadDraftRecordAsync(
            connection, transaction, scopeId, rowId, false, cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return Failure<EquipmentDiagnosticJobSnapshot>("staging.row_not_found", "Staging row was not found.");
        }

        if (draft.Value.Snapshot.Errors.Count > 0)
        {
            return Failure<EquipmentDiagnosticJobSnapshot>(
                "staging.validation",
                "A diagnostic job requires a structurally valid staging row.");
        }

        var jobId = Guid.CreateVersion7();
        var manifest = EquipmentCommissioningTools.BuildDiagnosticManifest(
            draft.Value.Snapshot, draft.Value.SecretReference);
        await using (var insert = new NpgsqlCommand(
                         $"""
                         INSERT INTO {EquipmentMigrations.Schema}.diagnostic_job
                             (job_id, row_id, scope_id, mode, status, fingerprint, manifest,
                              secret_reference, available_at, attempts, created_at)
                         VALUES
                             (@job_id, @row_id, @scope_id, @mode, 1, @fingerprint, @manifest,
                              @secret_reference, @available_at, 0, @created_at);
                         """,
                         connection,
                         transaction))
        {
            insert.Parameters.AddWithValue("job_id", jobId);
            insert.Parameters.AddWithValue("row_id", rowId);
            insert.Parameters.AddWithValue("scope_id", scopeId.Value);
            insert.Parameters.AddWithValue("mode", (short)mode);
            insert.Parameters.AddWithValue("fingerprint", draft.Value.Snapshot.Fingerprint);
            insert.Parameters.AddWithValue("manifest", NpgsqlDbType.Jsonb, manifest);
            insert.Parameters.AddWithValue(
                "secret_reference", (object?)draft.Value.SecretReference ?? DBNull.Value);
            insert.Parameters.AddWithValue("available_at", now);
            insert.Parameters.AddWithValue("created_at", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertCommissioningAuditAsync(
            connection, transaction, authorization, scopeId, rowId, jobId,
            mode == EquipmentDiagnosticMode.ConnectionTest
                ? "diagnostic.connection_test_enqueue"
                : "diagnostic.sample_poll_enqueue",
            null,
            cancellationToken).ConfigureAwait(false);
        var job = await ReadDiagnosticAsync(
            connection, transaction, scopeId, jobId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(job!);
    }

    public async Task<Result<EquipmentDiagnosticJobSnapshot>> ReadDiagnosticAsync(
        FacilityScopeId scopeId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var job = await ReadDiagnosticAsync(
            connection, transaction, scopeId, jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return Failure<EquipmentDiagnosticJobSnapshot>(
                "diagnostic.job_not_found",
                "Diagnostic job was not found.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(job);
    }

    public async Task<Result<EquipmentDiagnosticJobSnapshot>> ReadLatestDiagnosticAsync(
        FacilityScopeId scopeId,
        Guid rowId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT job_id
            FROM {EquipmentMigrations.Schema}.diagnostic_job
            WHERE scope_id = @scope_id AND row_id = @row_id
            ORDER BY created_at DESC, job_id DESC
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("row_id", rowId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not Guid jobId)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Failure<EquipmentDiagnosticJobSnapshot>(
                "diagnostic.job_not_found",
                "Diagnostic job was not found.");
        }

        var job = await ReadDiagnosticAsync(
            connection, transaction, scopeId, jobId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return job is null
            ? Failure<EquipmentDiagnosticJobSnapshot>(
                "diagnostic.job_not_found",
                "Diagnostic job was not found.")
            : Result.Success(job);
    }

    public async Task<Result<EquipmentDiagnosticJobClaim>> ClaimDiagnosticAsync(
        FacilityScopeId scopeId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        var now = clock.GetUtcNow();
        var leaseToken = Guid.CreateVersion7();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            WITH candidate AS (
                SELECT job_id
                FROM {EquipmentMigrations.Schema}.diagnostic_job
                WHERE scope_id = @scope_id
                  AND completed_at IS NULL
                  AND available_at <= @now
                  AND (lease_until IS NULL OR lease_until <= @now)
                ORDER BY available_at, job_id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE {EquipmentMigrations.Schema}.diagnostic_job AS job
            SET status = 2,
                claimed_by = @worker_id,
                lease_token = @lease_token,
                lease_until = @lease_until,
                attempts = attempts + 1,
                started_at = coalesce(started_at, @now)
            FROM candidate
            WHERE job.job_id = candidate.job_id
            RETURNING job.job_id, job.row_id, job.scope_id, job.mode, job.fingerprint,
                      job.manifest::text, job.secret_reference, job.lease_until, job.attempts;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("worker_id", workerId);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue("lease_until", now.Add(leaseDuration));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        EquipmentDiagnosticJobClaim? claim = null;
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            claim = new EquipmentDiagnosticJobClaim(
                reader.GetGuid(0),
                reader.GetGuid(1),
                FacilityScopeId.From(reader.GetGuid(2)),
                (EquipmentDiagnosticMode)reader.GetInt16(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                leaseToken,
                workerId,
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetInt32(8));
        }

        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claim is null
            ? Failure<EquipmentDiagnosticJobClaim>(
                "diagnostic.job_not_available",
                "No diagnostic job is currently available.")
            : Result.Success(claim);
    }

    public async Task<Result<EquipmentDiagnosticJobSnapshot>> CompleteDiagnosticAsync(
        EquipmentDiagnosticJobClaim claim,
        EquipmentDiagnosticJobStatus status,
        string? outcomeCode,
        string? outcomeMessage,
        IReadOnlyList<EquipmentDiagnosticSample> samples,
        CancellationToken cancellationToken = default)
    {
        if (status is EquipmentDiagnosticJobStatus.Pending or EquipmentDiagnosticJobStatus.Running)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentNullException.ThrowIfNull(samples);
        var now = clock.GetUtcNow();
        var resultJson = JsonSerializer.Serialize(samples.Take(256));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {EquipmentMigrations.Schema}.diagnostic_job
            SET status = @status,
                completed_at = @completed_at,
                outcome_code = @outcome_code,
                outcome_message = @outcome_message,
                result = @result,
                lease_until = NULL
            WHERE job_id = @job_id
              AND claimed_by = @worker_id
              AND lease_token = @lease_token
              AND completed_at IS NULL
              AND lease_until > @completed_at;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("status", (short)status);
        command.Parameters.AddWithValue("completed_at", now);
        command.Parameters.AddWithValue("outcome_code", (object?)outcomeCode ?? DBNull.Value);
        command.Parameters.AddWithValue("outcome_message", (object?)outcomeMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("result", NpgsqlDbType.Jsonb, resultJson);
        command.Parameters.AddWithValue("job_id", claim.JobId);
        command.Parameters.AddWithValue("worker_id", claim.ClaimedBy);
        command.Parameters.AddWithValue("lease_token", claim.LeaseToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            return Failure<EquipmentDiagnosticJobSnapshot>(
                "diagnostic.lease_invalid",
                "Diagnostic job lease is no longer valid.");
        }

        var snapshot = await ReadDiagnosticAsync(
            connection, transaction, claim.ScopeId, claim.JobId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(snapshot!);
    }

    public async Task<Result<char[]>> ResolveProtocolSecretAsync(
        FacilityScopeId scopeId,
        string reference,
        StagingSecretProtector protector,
        CancellationToken cancellationToken = default)
    {
        if (!reference.StartsWith("db:", StringComparison.Ordinal) ||
            !Guid.TryParseExact(reference.AsSpan(3), "N", out var secretId))
        {
            return Failure<char[]>("diagnostic.secret_reference", "Secret reference is invalid.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT protected_value
            FROM {EquipmentMigrations.Schema}.protocol_secret
            WHERE secret_id = @secret_id AND scope_id = @scope_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("secret_id", secretId);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        var protectedValue = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[];
        if (protectedValue is null)
        {
            return Failure<char[]>("diagnostic.secret_unavailable", "Referenced secret is unavailable.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(protector.Unprotect(protectedValue));
    }

    private static string SerializeForm(EquipmentStagingDraftInput input) =>
        JsonSerializer.Serialize(new
        {
            input.Host,
            input.Port,
            input.ModbusUnitId,
            input.ModbusTable,
            input.ModbusAddress,
            input.ModbusValueType,
            input.ModbusByteOrder,
            input.ModbusWordOrder,
            input.ModbusScale,
            input.SnmpVersion,
            input.SnmpOid,
            input.SnmpValueType,
            input.Unit,
        });

    private static async Task<(EquipmentStagingDraftSnapshot Snapshot, string? SecretReference)?>
        ReadDraftRecordAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            FacilityScopeId scopeId,
            Guid rowId,
            bool forUpdate,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT draft.row_id, draft.equipment_id, draft.scope_id, draft.location_id,
                   draft.code, draft.name, draft.protocol, draft.form_data::text,
                   draft.secret_reference, draft.apply_action, draft.update_authorized,
                   EXISTS (
                       SELECT 1 FROM {EquipmentMigrations.Schema}.equipment equipment
                       WHERE equipment.scope_id = draft.scope_id
                         AND (equipment.equipment_id = draft.equipment_id OR equipment.code = draft.code)
                   ),
                   draft.fingerprint, draft.version, draft.applied_at
            FROM {EquipmentMigrations.Schema}.staging_draft draft
            WHERE draft.scope_id = @scope_id AND draft.row_id = @row_id
            {(forUpdate ? "FOR UPDATE" : string.Empty)};
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("row_id", rowId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        using var form = JsonDocument.Parse(reader.GetString(7));
        var root = form.RootElement;
        var protocol = reader.GetString(6) == "modbus_tcp"
            ? EquipmentProtocol.ModbusTcp
            : EquipmentProtocol.Snmp;
        var secretReference = reader.IsDBNull(8) ? null : reader.GetString(8);
        var input = new EquipmentStagingDraftInput(
            reader.GetGuid(0),
            EquipmentId.From(reader.GetGuid(1)),
            FacilityScopeId.From(reader.GetGuid(2)),
            LocationId.From(reader.GetGuid(3)),
            reader.GetString(4),
            reader.GetString(5),
            protocol,
            root.GetProperty("Host").GetString() ?? string.Empty,
            root.GetProperty("Port").GetInt32(),
            NullableInt(root, "ModbusUnitId"),
            NullableString(root, "ModbusTable"),
            NullableInt(root, "ModbusAddress"),
            NullableString(root, "ModbusValueType"),
            NullableString(root, "ModbusByteOrder"),
            NullableString(root, "ModbusWordOrder"),
            NullableDecimal(root, "ModbusScale"),
            NullableString(root, "SnmpVersion"),
            NullableString(root, "SnmpOid"),
            NullableString(root, "SnmpValueType"),
            root.GetProperty("Unit").GetString() ?? string.Empty,
            null,
            (StagingApplyAction)reader.GetInt16(9),
            reader.GetInt64(13));
        var snapshot = new EquipmentStagingDraftSnapshot(
            input.RowId,
            input.EquipmentId,
            input.ScopeId,
            input.LocationId,
            input.Code,
            input.Name,
            input.Protocol,
            input.Host,
            input.Port,
            input.ModbusUnitId,
            input.ModbusTable,
            input.ModbusAddress,
            input.ModbusValueType,
            input.ModbusByteOrder,
            input.ModbusWordOrder,
            input.ModbusScale,
            input.SnmpVersion,
            input.SnmpOid,
            input.SnmpValueType,
            input.Unit,
            secretReference is not null,
            input.Action,
            reader.GetBoolean(10),
            reader.GetBoolean(11),
            reader.GetString(12),
            reader.GetInt64(13),
            reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
            EquipmentCommissioningTools.ValidateDraft(input, secretReference is not null));
        return (snapshot, secretReference);
    }

    private static async Task<EquipmentDiagnosticJobSnapshot?> ReadDiagnosticAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FacilityScopeId scopeId,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT job.job_id, job.row_id, job.scope_id, job.mode, job.status,
                   job.fingerprint,
                   coalesce(draft.fingerprint <> job.fingerprint, true),
                   job.attempts, job.created_at, job.started_at, job.completed_at,
                   job.outcome_code, job.outcome_message, job.result::text
            FROM {EquipmentMigrations.Schema}.diagnostic_job job
            LEFT JOIN {EquipmentMigrations.Schema}.staging_draft draft
              ON draft.row_id = job.row_id AND draft.scope_id = job.scope_id
            WHERE job.scope_id = @scope_id AND job.job_id = @job_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("job_id", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var samples = reader.IsDBNull(13)
            ? []
            : JsonSerializer.Deserialize<EquipmentDiagnosticSample[]>(reader.GetString(13)) ?? [];
        return new EquipmentDiagnosticJobSnapshot(
            reader.GetGuid(0),
            reader.GetGuid(1),
            FacilityScopeId.From(reader.GetGuid(2)),
            (EquipmentDiagnosticMode)reader.GetInt16(3),
            (EquipmentDiagnosticJobStatus)reader.GetInt16(4),
            reader.GetString(5),
            reader.GetBoolean(6),
            reader.GetInt32(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            samples);
    }

    private static async Task<EquipmentStagingTemplateSnapshot?> ReadTemplateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FacilityScopeId scopeId,
        Guid templateId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT template_id, scope_id, name, protocol, form_data::text, version
            FROM {EquipmentMigrations.Schema}.staging_template
            WHERE scope_id = @scope_id AND template_id = @template_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("template_id", templateId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        using var form = JsonDocument.Parse(reader.GetString(4));
        var root = form.RootElement;
        return new EquipmentStagingTemplateSnapshot(
            reader.GetGuid(0),
            FacilityScopeId.From(reader.GetGuid(1)),
            reader.GetString(2),
            reader.GetString(3) == "modbus_tcp"
                ? EquipmentProtocol.ModbusTcp
                : EquipmentProtocol.Snmp,
            root.GetProperty("Port").GetInt32(),
            NullableString(root, "ModbusTable"),
            NullableInt(root, "ModbusAddress"),
            NullableString(root, "ModbusValueType"),
            NullableString(root, "ModbusByteOrder"),
            NullableString(root, "ModbusWordOrder"),
            NullableDecimal(root, "ModbusScale"),
            NullableString(root, "SnmpVersion"),
            NullableString(root, "SnmpOid"),
            NullableString(root, "SnmpValueType"),
            root.GetProperty("Unit").GetString() ?? string.Empty,
            reader.GetInt64(5));
    }

    private async Task InsertCommissioningAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AuthorizedAccess authorization,
        FacilityScopeId scopeId,
        Guid? rowId,
        Guid? jobId,
        string action,
        long? version,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {EquipmentMigrations.Schema}.commissioning_audit
                (audit_id, scope_id, row_id, job_id, session_id, subject_id,
                 permission, action, resulting_version, changed_at)
            VALUES
                (@audit_id, @scope_id, @row_id, @job_id, @session_id, @subject_id,
                 @permission, @action, @version, @changed_at);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("audit_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("scope_id", scopeId.Value);
        command.Parameters.AddWithValue("row_id", (object?)rowId ?? DBNull.Value);
        command.Parameters.AddWithValue("job_id", (object?)jobId ?? DBNull.Value);
        command.Parameters.AddWithValue("session_id", authorization.Session.Id.Value);
        command.Parameters.AddWithValue("subject_id", authorization.Session.SubjectId.Value);
        command.Parameters.AddWithValue("permission", authorization.Permission.Value);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("version", (object?)version ?? DBNull.Value);
        command.Parameters.AddWithValue("changed_at", clock.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static decimal? NullableDecimal(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : root.GetProperty(name).GetDecimal();

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));
}
