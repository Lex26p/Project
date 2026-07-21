using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dispatcher.Events;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Npgsql;

namespace Dispatcher.Incidents;

public sealed class IncidentStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;

    public IncidentStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock)
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

    public async Task<Result<IncidentCommandResult<IncidentSnapshot>>> CreateAsync(
        AuthorizedMutation authorization,
        CreateIncidentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateCommand(authorization, IncidentPermissions.Create, request.Summary, request.IdempotencyKey);
        if (validation.IsFailure)
        {
            return Result.Failure<IncidentCommandResult<IncidentSnapshot>>(validation.Error!);
        }

        var now = UtcNow();
        var dto = new IncidentDto(
            request.IncidentId.Value, request.Summary.Trim(), request.CoordinatorPersonId.Value,
            StateVersion.Initial.Value, now, now, []);
        var fingerprint = Fingerprint(JsonSerializer.Serialize(new
        {
            IncidentId = request.IncidentId.Value,
            Summary = request.Summary.Trim(),
            Coordinator = request.CoordinatorPersonId.Value,
        }));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var replay = await ReadReceiptAsync<IncidentDto>(
            connection, transaction, request.IdempotencyKey, "create", fingerprint, cancellationToken).ConfigureAwait(false);
        if (replay.IsFailure)
        {
            return Result.Failure<IncidentCommandResult<IncidentSnapshot>>(replay.Error!);
        }

        if (replay.Value.Value is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(new IncidentCommandResult<IncidentSnapshot>(
                replay.Value.Value.ToModel(), IncidentCommandDisposition.Replay));
        }

        await using (var command = new NpgsqlCommand(
            $"""
            INSERT INTO {IncidentMigrations.Schema}.incident
                (incident_id, summary, coordinator_person_id, version, created_at, updated_at)
            VALUES (@id, @summary, @coordinator, 1, @now, @now);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", request.IncidentId.Value);
            command.Parameters.AddWithValue("summary", request.Summary.Trim());
            command.Parameters.AddWithValue("coordinator", request.CoordinatorPersonId.Value);
            command.Parameters.AddWithValue("now", now);
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Failure<IncidentCommandResult<IncidentSnapshot>>(
                    "incident.identity_conflict", "Incident identity already exists.");
            }
        }

        await WriteReceiptAndAuditAsync(
            connection, transaction, request.IdempotencyKey, "create", fingerprint, dto,
            authorization, request.IncidentId, null, StateVersion.Initial, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new IncidentCommandResult<IncidentSnapshot>(dto.ToModel(), IncidentCommandDisposition.Applied));
    }

    public async Task<Result<IncidentCommandResult<IncidentSnapshot>>> LinkEventAsync(
        AuthorizedMutation authorization,
        LinkIncidentEventRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateCommand(authorization, IncidentPermissions.LinkSource, "source", request.IdempotencyKey);
        if (validation.IsFailure || !request.ExpectedVersion.IsDefined)
        {
            return validation.IsFailure
                ? Result.Failure<IncidentCommandResult<IncidentSnapshot>>(validation.Error!)
                : Failure<IncidentCommandResult<IncidentSnapshot>>("incident.version", "Expected version is required.");
        }

        var fingerprint = Fingerprint(JsonSerializer.Serialize(new
        {
            IncidentId = request.IncidentId.Value,
            EventId = request.Event.EventId.Value,
            ExpectedVersion = request.ExpectedVersion.Value,
        }));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var replay = await ReadReceiptAsync<IncidentDto>(
            connection, transaction, request.IdempotencyKey, "link-source", fingerprint, cancellationToken).ConfigureAwait(false);
        if (replay.IsFailure)
        {
            return Result.Failure<IncidentCommandResult<IncidentSnapshot>>(replay.Error!);
        }

        if (replay.Value.Value is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(new IncidentCommandResult<IncidentSnapshot>(replay.Value.Value.ToModel(), IncidentCommandDisposition.Replay));
        }

        var incident = await ReadIncidentDtoAsync(connection, transaction, request.IncidentId, true, cancellationToken)
            .ConfigureAwait(false);
        if (incident is null)
        {
            return Failure<IncidentCommandResult<IncidentSnapshot>>("incident.not_found", "Incident was not found.");
        }

        if (incident.Version != request.ExpectedVersion.Value)
        {
            return Failure<IncidentCommandResult<IncidentSnapshot>>("incident.version", "Incident version is stale.");
        }

        var link = new SourceLinkDto(
            IncidentSourceLinkId.New().Value, request.Event.EventId.Value, request.Event.OccurrenceId.Value,
            request.Event.ScopeId.Value, request.Event.PointId.Value,
            $"/events/{request.Event.EventId.Value}",
            ["events.dispatcher.read", $"runtime.point.p{request.Event.PointId.Value:N}.read"]);
        await using (var command = new NpgsqlCommand(
            $"""
            INSERT INTO {IncidentMigrations.Schema}.source_link
                (link_id, incident_id, event_id, occurrence_id, scope_id, point_id, route, required_permissions)
            VALUES (@link, @incident, @event, @occurrence, @scope, @point, @route, @permissions);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("link", link.LinkId);
            command.Parameters.AddWithValue("incident", request.IncidentId.Value);
            command.Parameters.AddWithValue("event", link.EventId);
            command.Parameters.AddWithValue("occurrence", link.OccurrenceId);
            command.Parameters.AddWithValue("scope", link.ScopeId);
            command.Parameters.AddWithValue("point", link.PointId);
            command.Parameters.AddWithValue("route", link.Route);
            command.Parameters.AddWithValue("permissions", link.RequiredPermissions);
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Failure<IncidentCommandResult<IncidentSnapshot>>(
                    "incident.source_exists", "The event is already linked to this incident.");
            }
        }

        var now = UtcNow();
        var next = incident with { Version = request.ExpectedVersion.Next().Value, UpdatedAt = now };
        next.SourceLinks.Add(link);
        await UpdateIncidentVersionAsync(connection, transaction, request.IncidentId, next.Version, now, cancellationToken)
            .ConfigureAwait(false);
        await WriteReceiptAndAuditAsync(
            connection, transaction, request.IdempotencyKey, "link-source", fingerprint, next,
            authorization, request.IncidentId, null, StateVersion.From(next.Version), now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new IncidentCommandResult<IncidentSnapshot>(next.ToModel(), IncidentCommandDisposition.Applied));
    }

    public async Task<Result<IncidentCommandResult<IncidentTaskSnapshot>>> CreateTaskAsync(
        AuthorizedMutation authorization,
        CreateIncidentTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateCommand(authorization, IncidentPermissions.ManageTask, request.Summary, request.IdempotencyKey);
        if (validation.IsFailure)
        {
            return Result.Failure<IncidentCommandResult<IncidentTaskSnapshot>>(validation.Error!);
        }

        var fingerprint = Fingerprint(JsonSerializer.Serialize(new
        {
            TaskId = request.TaskId.Value, IncidentId = request.IncidentId.Value,
            Summary = request.Summary.Trim(), AssignedTo = request.AssignedPersonId.Value,
            ExpectedVersion = request.ExpectedIncidentVersion.Value,
        }));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var replay = await ReadReceiptAsync<TaskDto>(
            connection, transaction, request.IdempotencyKey, "create-task", fingerprint, cancellationToken).ConfigureAwait(false);
        if (replay.IsFailure)
        {
            return Result.Failure<IncidentCommandResult<IncidentTaskSnapshot>>(replay.Error!);
        }

        if (replay.Value.Value is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(new IncidentCommandResult<IncidentTaskSnapshot>(replay.Value.Value.ToModel(), IncidentCommandDisposition.Replay));
        }

        var incident = await ReadIncidentDtoAsync(connection, transaction, request.IncidentId, true, cancellationToken)
            .ConfigureAwait(false);
        if (incident is null || incident.Version != request.ExpectedIncidentVersion.Value)
        {
            return Failure<IncidentCommandResult<IncidentTaskSnapshot>>(
                incident is null ? "incident.not_found" : "incident.version",
                incident is null ? "Incident was not found." : "Incident version is stale.");
        }

        var now = UtcNow();
        var task = new TaskDto(
            request.TaskId.Value, request.IncidentId.Value, request.Summary.Trim(), request.AssignedPersonId.Value,
            (int)IncidentTaskState.Offered, StateVersion.Initial.Value, now);
        await WriteTaskAsync(connection, transaction, task, insert: true, cancellationToken).ConfigureAwait(false);
        var incidentVersion = StateVersion.From(incident.Version).Next();
        await UpdateIncidentVersionAsync(connection, transaction, request.IncidentId, incidentVersion.Value, now, cancellationToken)
            .ConfigureAwait(false);
        await WriteReceiptAndAuditAsync(
            connection, transaction, request.IdempotencyKey, "create-task", fingerprint, task,
            authorization, request.IncidentId, request.TaskId, StateVersion.Initial, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new IncidentCommandResult<IncidentTaskSnapshot>(task.ToModel(), IncidentCommandDisposition.Applied));
    }

    public Task<Result<IncidentCommandResult<IncidentTaskSnapshot>>> AcceptTaskAsync(
        AuthorizedMutation authorization, PersonId actor, TransitionIncidentTaskRequest request,
        CancellationToken cancellationToken = default) =>
        TransitionTaskAsync(authorization, actor, request, "accept-task", IncidentTaskState.Accepted, null, cancellationToken);

    public Task<Result<IncidentCommandResult<IncidentTaskSnapshot>>> TransferTaskAsync(
        AuthorizedMutation authorization, PersonId actor, TransitionIncidentTaskRequest request,
        CancellationToken cancellationToken = default) =>
        TransitionTaskAsync(authorization, actor, request, "transfer-task", IncidentTaskState.Offered, request.TransferTo, cancellationToken);

    public Task<Result<IncidentCommandResult<IncidentTaskSnapshot>>> ReturnTaskAsync(
        AuthorizedMutation authorization, PersonId actor, TransitionIncidentTaskRequest request,
        CancellationToken cancellationToken = default) =>
        TransitionTaskAsync(authorization, actor, request, "return-task", IncidentTaskState.Returned, null, cancellationToken);

    public async Task<IncidentSnapshot?> ReadIncidentAsync(
        IncidentId incidentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var dto = await ReadIncidentDtoAsync(connection, transaction, incidentId, false, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return dto?.ToModel();
    }

    public async Task<IncidentTaskSnapshot?> ReadTaskAsync(
        IncidentTaskId taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var dto = await ReadTaskDtoAsync(connection, transaction, taskId, false, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return dto?.ToModel();
    }

    public async Task<IReadOnlyList<IncidentTaskSnapshot>> ReadAllTasksAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<IncidentTaskSnapshot>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT task_id, incident_id, summary, assigned_person_id, state, version, updated_at FROM {IncidentMigrations.Schema}.task ORDER BY task_id;",
            connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadTask(reader).ToModel());
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    private async Task<Result<IncidentCommandResult<IncidentTaskSnapshot>>> TransitionTaskAsync(
        AuthorizedMutation authorization, PersonId actor, TransitionIncidentTaskRequest request,
        string action, IncidentTaskState state, PersonId? assignedTo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);
        var validation = ValidateCommand(authorization, IncidentPermissions.TransitionTask, action, request.IdempotencyKey);
        if (validation.IsFailure || !request.ExpectedVersion.IsDefined)
        {
            return validation.IsFailure
                ? Result.Failure<IncidentCommandResult<IncidentTaskSnapshot>>(validation.Error!)
                : Failure<IncidentCommandResult<IncidentTaskSnapshot>>("incident.task_version", "Expected task version is required.");
        }

        if (action == "transfer-task" && (assignedTo is null || assignedTo == actor))
        {
            return Failure<IncidentCommandResult<IncidentTaskSnapshot>>(
                "incident.task_transfer", "Transfer requires another target person.");
        }

        var fingerprint = Fingerprint(JsonSerializer.Serialize(new
        {
            TaskId = request.TaskId.Value,
            ExpectedVersion = request.ExpectedVersion.Value,
            Actor = actor.Value,
            TransferTo = assignedTo?.Value,
        }));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var replay = await ReadReceiptAsync<TaskDto>(
            connection, transaction, request.IdempotencyKey, action, fingerprint, cancellationToken).ConfigureAwait(false);
        if (replay.IsFailure)
        {
            return Result.Failure<IncidentCommandResult<IncidentTaskSnapshot>>(replay.Error!);
        }

        if (replay.Value.Value is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(new IncidentCommandResult<IncidentTaskSnapshot>(replay.Value.Value.ToModel(), IncidentCommandDisposition.Replay));
        }

        var current = await ReadTaskDtoAsync(connection, transaction, request.TaskId, true, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return Failure<IncidentCommandResult<IncidentTaskSnapshot>>("incident.task_not_found", "Incident task was not found.");
        }

        if (current.Version != request.ExpectedVersion.Value)
        {
            return Failure<IncidentCommandResult<IncidentTaskSnapshot>>("incident.task_version", "Incident task version is stale.");
        }

        if (current.AssignedPersonId != actor.Value)
        {
            return Failure<IncidentCommandResult<IncidentTaskSnapshot>>(
                "permission.denied", "Only the current assignee may transition the task.");
        }

        if (action == "accept-task" && current.State != (int)IncidentTaskState.Offered)
        {
            return Failure<IncidentCommandResult<IncidentTaskSnapshot>>("incident.task_state", "Only an offered task can be accepted.");
        }

        var target = assignedTo?.Value;
        if (action == "return-task")
        {
            target = await ReadCoordinatorAsync(connection, transaction, IncidentId.From(current.IncidentId), cancellationToken)
                .ConfigureAwait(false);
        }

        var now = UtcNow();
        var next = current with
        {
            AssignedPersonId = target ?? current.AssignedPersonId,
            State = (int)state,
            Version = request.ExpectedVersion.Next().Value,
            UpdatedAt = now,
        };
        await WriteTaskAsync(connection, transaction, next, insert: false, cancellationToken).ConfigureAwait(false);
        await WriteReceiptAndAuditAsync(
            connection, transaction, request.IdempotencyKey, action, fingerprint, next,
            authorization, IncidentId.From(next.IncidentId), request.TaskId, StateVersion.From(next.Version), now, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new IncidentCommandResult<IncidentTaskSnapshot>(next.ToModel(), IncidentCommandDisposition.Applied));
    }

    private static Result ValidateCommand(
        AuthorizedMutation authorization, PermissionCode permission, string content, string idempotencyKey)
    {
        if (authorization.Permission != permission)
        {
            return Failure("incident.permission_contract", "Authorization does not match the Incident command.");
        }

        if (string.IsNullOrWhiteSpace(content) || content.Trim().Length > 500 ||
            string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
        {
            return Failure("incident.command_invalid", "Incident command content is invalid.");
        }

        return Result.Success();
    }

    private static async Task<Result<Receipt<TDto>>> ReadReceiptAsync<TDto>(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string key, string action,
        string fingerprint, CancellationToken cancellationToken)
        where TDto : class
    {
        await using var command = new NpgsqlCommand(
            $"SELECT action, fingerprint, result_snapshot::text FROM {IncidentMigrations.Schema}.command_receipt WHERE idempotency_key = @key;",
            connection, transaction);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Result.Success(new Receipt<TDto>(null));
        }

        var existingAction = reader.GetString(0);
        var existingFingerprint = reader.GetString(1);
        var json = reader.GetString(2);
        if (!string.Equals(existingAction, action, StringComparison.Ordinal) ||
            !string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return Failure<Receipt<TDto>>("incident.idempotency_conflict", "Idempotency key was used for another command.");
        }

        return Result.Success(new Receipt<TDto>(JsonSerializer.Deserialize<TDto>(json)!));
    }

    private static async Task<IncidentDto?> ReadIncidentDtoAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IncidentId incidentId, bool forUpdate,
        CancellationToken cancellationToken)
    {
        IncidentDto? incident;
        await using (var command = new NpgsqlCommand(
            $"SELECT summary, coordinator_person_id, version, created_at, updated_at FROM {IncidentMigrations.Schema}.incident WHERE incident_id = @id{(forUpdate ? " FOR UPDATE" : string.Empty)};",
            connection, transaction))
        {
            command.Parameters.AddWithValue("id", incidentId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            incident = new IncidentDto(
                incidentId.Value, reader.GetString(0), reader.GetGuid(1), checked((ulong)reader.GetInt64(2)),
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4), []);
        }

        await using (var command = new NpgsqlCommand(
            $"SELECT link_id, event_id, occurrence_id, scope_id, point_id, route, required_permissions FROM {IncidentMigrations.Schema}.source_link WHERE incident_id = @id ORDER BY link_id;",
            connection, transaction))
        {
            command.Parameters.AddWithValue("id", incidentId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                incident.SourceLinks.Add(new SourceLinkDto(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4),
                    reader.GetString(5), reader.GetFieldValue<string[]>(6)));
            }
        }

        return incident;
    }

    private static async Task<TaskDto?> ReadTaskDtoAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IncidentTaskId taskId, bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT task_id, incident_id, summary, assigned_person_id, state, version, updated_at FROM {IncidentMigrations.Schema}.task WHERE task_id = @id{(forUpdate ? " FOR UPDATE" : string.Empty)};",
            connection, transaction);
        command.Parameters.AddWithValue("id", taskId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTask(reader) : null;
    }

    private static TaskDto ReadTask(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3), reader.GetInt16(4),
        checked((ulong)reader.GetInt64(5)), reader.GetFieldValue<DateTimeOffset>(6));

    private static async Task<Guid> ReadCoordinatorAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IncidentId incidentId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT coordinator_person_id FROM {IncidentMigrations.Schema}.incident WHERE incident_id = @id;",
            connection, transaction);
        command.Parameters.AddWithValue("id", incidentId.Value);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task UpdateIncidentVersionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IncidentId incidentId, ulong version,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"UPDATE {IncidentMigrations.Schema}.incident SET version = @version, updated_at = @now WHERE incident_id = @id;",
            connection, transaction);
        command.Parameters.AddWithValue("id", incidentId.Value);
        command.Parameters.AddWithValue("version", checked((long)version));
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTaskAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, TaskDto task, bool insert,
        CancellationToken cancellationToken)
    {
        var sql = insert
            ? $"""
              INSERT INTO {IncidentMigrations.Schema}.task
                  (task_id, incident_id, summary, assigned_person_id, state, version, updated_at)
              VALUES (@task, @incident, @summary, @assigned, @state, @version, @updated);
              """
            : $"""
              UPDATE {IncidentMigrations.Schema}.task SET assigned_person_id = @assigned, state = @state,
                  version = @version, updated_at = @updated WHERE task_id = @task;
              """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("task", task.TaskId);
        if (insert)
        {
            command.Parameters.AddWithValue("incident", task.IncidentId);
            command.Parameters.AddWithValue("summary", task.Summary);
        }

        command.Parameters.AddWithValue("assigned", task.AssignedPersonId);
        command.Parameters.AddWithValue("state", checked((short)task.State));
        command.Parameters.AddWithValue("version", checked((long)task.Version));
        command.Parameters.AddWithValue("updated", task.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteReceiptAndAuditAsync<TDto>(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string key, string action, string fingerprint,
        TDto result, AuthorizedMutation authorization, IncidentId incidentId, IncidentTaskId? taskId,
        StateVersion resultingVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand(
            $"""
            INSERT INTO {IncidentMigrations.Schema}.command_receipt
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
            INSERT INTO {IncidentMigrations.Schema}.mutation_audit
                (audit_id, incident_id, task_id, session_id, subject_id, action, resulting_version, changed_at)
            VALUES (@audit, @incident, @task, @session, @subject, @action, @version, @now);
            """, connection, transaction);
        audit.Parameters.AddWithValue("audit", Guid.NewGuid());
        audit.Parameters.AddWithValue("incident", incidentId.Value);
        audit.Parameters.AddWithValue("task", taskId is null ? DBNull.Value : taskId.Value.Value);
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
            : throw new InvalidOperationException("Incident persistence requires a UTC wall clock.");
    }

    private static string Fingerprint(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));

    private sealed record SourceLinkDto(
        Guid LinkId, Guid EventId, Guid OccurrenceId, Guid ScopeId, Guid PointId,
        string Route, string[] RequiredPermissions)
    {
        public IncidentSourceLink ToModel() => new(
            IncidentSourceLinkId.From(LinkId), OperationalEventId.From(EventId),
            Dispatcher.Alarm.AlarmOccurrenceId.From(OccurrenceId), Dispatcher.Core.RuntimeScopeId.From(ScopeId),
            Dispatcher.Semantics.PointId.From(PointId), Route,
            RequiredPermissions.Select(PermissionCode.From).ToArray());
    }

    private sealed record IncidentDto(
        Guid IncidentId, string Summary, Guid CoordinatorPersonId, ulong Version,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, List<SourceLinkDto> SourceLinks)
    {
        public IncidentSnapshot ToModel() => new(
            Dispatcher.Incidents.IncidentId.From(IncidentId), Summary, PersonId.From(CoordinatorPersonId),
            StateVersion.From(Version), CreatedAt, UpdatedAt, SourceLinks.Select(value => value.ToModel()).ToArray());
    }

    private sealed record TaskDto(
        Guid TaskId, Guid IncidentId, string Summary, Guid AssignedPersonId,
        int State, ulong Version, DateTimeOffset UpdatedAt)
    {
        public IncidentTaskSnapshot ToModel() => new(
            IncidentTaskId.From(TaskId), Dispatcher.Incidents.IncidentId.From(IncidentId), Summary,
            PersonId.From(AssignedPersonId), (IncidentTaskState)State, StateVersion.From(Version), UpdatedAt);
    }

    private sealed record Receipt<T>(T? Value) where T : class;
}
