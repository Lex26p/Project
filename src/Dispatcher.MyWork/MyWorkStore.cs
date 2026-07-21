using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Npgsql;

namespace Dispatcher.MyWork;

public sealed class MyWorkStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;

    public MyWorkStore(NpgsqlDataSource dataSource, string databaseRole)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRole);
        if (databaseRole.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new ArgumentException("Invalid PostgreSQL role name.", nameof(databaseRole));
        }

        this.databaseRole = databaseRole;
    }

    public async Task<Result<WorkAssignmentProjection>> AcceptAsync(
        WorkAssignmentProjection projection, CancellationToken cancellationToken = default)
    {
        var validation = Validate(projection);
        if (validation.IsFailure)
        {
            return Result.Failure<WorkAssignmentProjection>(validation.Error!);
        }

        var dto = ProjectionDto.From(projection);
        var fingerprint = Fingerprint(JsonSerializer.Serialize(dto));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var current = await ReadHeadAsync(
            connection, transaction, projection.SourceOwner, projection.SourceItemId, cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            if (current.Value.Version > projection.SourceVersion.Value)
            {
                return Failure<WorkAssignmentProjection>("my_work.source_version", "Source assignment version regressed.");
            }

            if (current.Value.Version == projection.SourceVersion.Value)
            {
                return string.Equals(current.Value.Fingerprint, fingerprint, StringComparison.Ordinal)
                    ? Result.Success(projection)
                    : Failure<WorkAssignmentProjection>("my_work.source_conflict", "Source version has different assignment content.");
            }
        }

        await WriteAsync(connection, transaction, dto, fingerprint, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(projection);
    }

    public async Task<Result> RebuildOwnerAsync(
        string sourceOwner, IReadOnlyCollection<WorkAssignmentProjection> projections,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceOwner);
        ArgumentNullException.ThrowIfNull(projections);
        if (projections.Any(value => !string.Equals(value.SourceOwner, sourceOwner, StringComparison.Ordinal)) ||
            projections.Select(value => value.SourceItemId).Distinct().Count() != projections.Count)
        {
            return Failure("my_work.rebuild_invalid", "Rebuild input must contain unique assignments from one owner.");
        }

        foreach (var projection in projections)
        {
            var validation = Validate(projection);
            if (validation.IsFailure)
            {
                return validation;
            }
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using (var delete = new NpgsqlCommand(
            $"DELETE FROM {MyWorkMigrations.Schema}.assignment_projection WHERE source_owner = @owner;",
            connection, transaction))
        {
            delete.Parameters.AddWithValue("owner", sourceOwner);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var projection in projections.OrderBy(value => value.SourceItemId))
        {
            var dto = ProjectionDto.From(projection);
            await WriteAsync(
                connection, transaction, dto, Fingerprint(JsonSerializer.Serialize(dto)), cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<IReadOnlyList<WorkAssignmentProjection>> ReadForPersonAsync(
        PersonId personId, CancellationToken cancellationToken = default)
    {
        var results = new List<WorkAssignmentProjection>();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT source_owner, source_kind, source_item_id, source_version, assigned_person_id,
                   summary, state, route, required_permissions, updated_at
            FROM {MyWorkMigrations.Schema}.assignment_projection
            WHERE assigned_person_id = @person
            ORDER BY updated_at DESC, source_owner, source_item_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("person", personId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ProjectionDto(
                reader.GetString(0), reader.GetString(1), reader.GetGuid(2), checked((ulong)reader.GetInt64(3)),
                reader.GetGuid(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetFieldValue<string[]>(8), reader.GetFieldValue<DateTimeOffset>(9)).ToModel());
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    private static Result Validate(WorkAssignmentProjection value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value.SourceOwner) || value.SourceOwner.Length > 100 ||
            string.IsNullOrWhiteSpace(value.SourceKind) || value.SourceKind.Length > 100 ||
            value.SourceItemId == Guid.Empty || !value.SourceVersion.IsDefined ||
            string.IsNullOrWhiteSpace(value.Summary) || value.Summary.Length > 500 ||
            string.IsNullOrWhiteSpace(value.State) || value.State.Length > 100 ||
            string.IsNullOrWhiteSpace(value.Route) || !value.Route.StartsWith('/') ||
            value.RequiredPermissions.Count == 0 || value.UpdatedAt.Offset != TimeSpan.Zero)
        {
            return Failure("my_work.projection_invalid", "Work assignment projection is invalid.");
        }

        return Result.Success();
    }

    private static async Task<(ulong Version, string Fingerprint)?> ReadHeadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string owner, Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT source_version, fingerprint FROM {MyWorkMigrations.Schema}.assignment_projection WHERE source_owner = @owner AND source_item_id = @item FOR UPDATE;",
            connection, transaction);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("item", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (checked((ulong)reader.GetInt64(0)), reader.GetString(1))
            : null;
    }

    private static async Task WriteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, ProjectionDto dto, string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {MyWorkMigrations.Schema}.assignment_projection
                (source_owner, source_kind, source_item_id, source_version, assigned_person_id,
                 summary, state, route, required_permissions, updated_at, fingerprint)
            VALUES (@owner, @kind, @item, @version, @person, @summary, @state, @route, @permissions, @updated, @fingerprint)
            ON CONFLICT (source_owner, source_item_id) DO UPDATE SET
                source_kind = EXCLUDED.source_kind,
                source_version = EXCLUDED.source_version,
                assigned_person_id = EXCLUDED.assigned_person_id,
                summary = EXCLUDED.summary,
                state = EXCLUDED.state,
                route = EXCLUDED.route,
                required_permissions = EXCLUDED.required_permissions,
                updated_at = EXCLUDED.updated_at,
                fingerprint = EXCLUDED.fingerprint;
            """, connection, transaction);
        command.Parameters.AddWithValue("owner", dto.SourceOwner);
        command.Parameters.AddWithValue("kind", dto.SourceKind);
        command.Parameters.AddWithValue("item", dto.SourceItemId);
        command.Parameters.AddWithValue("version", checked((long)dto.SourceVersion));
        command.Parameters.AddWithValue("person", dto.AssignedPersonId);
        command.Parameters.AddWithValue("summary", dto.Summary);
        command.Parameters.AddWithValue("state", dto.State);
        command.Parameters.AddWithValue("route", dto.Route);
        command.Parameters.AddWithValue("permissions", dto.RequiredPermissions);
        command.Parameters.AddWithValue("updated", dto.UpdatedAt);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SetRoleAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE {databaseRole};", connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Fingerprint(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));

    private sealed record ProjectionDto(
        string SourceOwner, string SourceKind, Guid SourceItemId, ulong SourceVersion,
        Guid AssignedPersonId, string Summary, string State, string Route,
        string[] RequiredPermissions, DateTimeOffset UpdatedAt)
    {
        public static ProjectionDto From(WorkAssignmentProjection value) => new(
            value.SourceOwner, value.SourceKind, value.SourceItemId, value.SourceVersion.Value,
            value.AssignedPersonId.Value, value.Summary, value.State, value.Route,
            value.RequiredPermissions.Select(permission => permission.Value).ToArray(), value.UpdatedAt);

        public WorkAssignmentProjection ToModel() => new(
            SourceOwner, SourceKind, SourceItemId, StateVersion.From(SourceVersion),
            PersonId.From(AssignedPersonId), Summary, State, Route,
            RequiredPermissions.Select(PermissionCode.From).ToArray(), UpdatedAt);
    }
}
