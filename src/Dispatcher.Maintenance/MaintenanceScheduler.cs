using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Workspace;
using Npgsql;
using NpgsqlTypes;

namespace Dispatcher.Maintenance;

public readonly record struct MaintenanceForecastObligationId
{
    private readonly CanonicalId<MaintenanceForecastObligationId> value;
    private MaintenanceForecastObligationId(CanonicalId<MaintenanceForecastObligationId> value) => this.value = value;
    public Guid Value => value.Value;
    public static MaintenanceForecastObligationId New() => new(CanonicalId.New<MaintenanceForecastObligationId>());
    public static MaintenanceForecastObligationId From(Guid value) => new(CanonicalId.From<MaintenanceForecastObligationId>(value));
}

public enum MaintenanceMaterializationState { Pending = 1, InFlight = 2, Completed = 3 }

public sealed record MaintenanceMaterializationPolicy(
    PersonId AssignedPersonId,
    WorkOrderSafetyFields Safety,
    IReadOnlyCollection<NewWorkOrderChecklistItem> Checklist);

public sealed record MaintenanceForecastObligation(
    MaintenanceForecastObligationId ObligationId,
    MaintenanceWorkOrderId WorkOrderId,
    MaintenancePlanId PlanId,
    MaintenanceAssetId AssetId,
    FacilityScopeId ScopeId,
    RevisionNumber PlanRevision,
    string Title,
    DateOnly DueOn,
    MaintenanceMaterializationPolicy Policy,
    MaintenanceMaterializationState State,
    string? ClaimedBy,
    DateTimeOffset? LeaseUntil,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    public bool IsOverdue(DateOnly today) => DueOn < today && State != MaintenanceMaterializationState.Completed;
}

public sealed record MaintenanceMaterializationRun(
    MaintenanceForecastObligation? Obligation,
    MaintenanceWorkOrderSnapshot? WorkOrder);

public sealed class MaintenanceSchedulerStore
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string databaseRole;
    private readonly IWallClock clock;

    public MaintenanceSchedulerStore(NpgsqlDataSource dataSource, string databaseRole, IWallClock clock)
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

    public async Task<Result<IReadOnlyList<MaintenanceForecastObligation>>> EnsureForecastAsync(
        AuthorizedMutation authorization,
        ApprovedMaintenancePlan plan,
        MaintenanceAssetSnapshot asset,
        DateOnly through,
        MaintenanceMaterializationPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(policy.Safety);
        ArgumentNullException.ThrowIfNull(policy.Checklist);
        if (plan.AssetId != asset.AssetId || authorization.Permission != MaintenanceWorkPermissions.Manage(asset.ScopeId) ||
            policy.Safety.AcknowledgedAt is not null || policy.Safety.Instructions?.Length > 1000 ||
            policy.Checklist.Select(value => value.ItemId).Distinct().Count() != policy.Checklist.Count ||
            policy.Checklist.Any(value => string.IsNullOrWhiteSpace(value.Description) || value.Description.Length > 500))
        {
            return Failure<IReadOnlyList<MaintenanceForecastObligation>>(
                "maintenance.forecast_contract", "Forecast materialization contract is invalid.");
        }

        if (through < plan.FirstDueOn)
        {
            return Result.Success<IReadOnlyList<MaintenanceForecastObligation>>([]);
        }

        var entries = MaintenancePlanCalendar.Query([plan], [asset], new(plan.FirstDueOn, through));
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var result = new List<MaintenanceForecastObligation>(entries.Count);
        foreach (var entry in entries)
        {
            var obligationId = MaintenanceForecastObligationId.New();
            var workOrderId = MaintenanceWorkOrderId.New();
            var fingerprint = Fingerprint(entry, asset.ScopeId, policy);
            await using (var insert = new NpgsqlCommand(
                $"""
                INSERT INTO {MaintenanceMigrations.Schema}.forecast_materialization
                    (obligation_id, work_order_id, plan_id, asset_id, scope_id, plan_revision, title, due_on,
                     assigned_person_id, permit_required, isolation_required, safety_instructions, checklist,
                     fingerprint, state, attempts, created_at)
                VALUES (@obligation, @work_order, @plan, @asset, @scope, @revision, @title, @due,
                        @assigned, @permit, @isolation, @instructions, CAST(@checklist AS jsonb),
                        @fingerprint, 1, 0, @now)
                ON CONFLICT (plan_id, due_on) DO NOTHING;
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("obligation", obligationId.Value);
                insert.Parameters.AddWithValue("work_order", workOrderId.Value);
                insert.Parameters.AddWithValue("plan", entry.PlanId.Value);
                insert.Parameters.AddWithValue("asset", entry.AssetId.Value);
                insert.Parameters.AddWithValue("scope", asset.ScopeId.Value);
                insert.Parameters.AddWithValue("revision", checked((long)entry.PlanRevision.Value));
                insert.Parameters.AddWithValue("title", entry.Title);
                insert.Parameters.AddWithValue("due", entry.DueOn);
                insert.Parameters.AddWithValue("assigned", policy.AssignedPersonId.Value);
                insert.Parameters.AddWithValue("permit", policy.Safety.PermitRequired);
                insert.Parameters.AddWithValue("isolation", policy.Safety.IsolationRequired);
                insert.Parameters.Add(new NpgsqlParameter("instructions", NpgsqlDbType.Text)
                {
                    Value = policy.Safety.Instructions?.Trim() ?? (object)DBNull.Value,
                });
                insert.Parameters.AddWithValue("checklist", JsonSerializer.Serialize(policy.Checklist
                    .OrderBy(value => value.ItemId.Value).Select(ChecklistDto.FromModel).ToArray()));
                insert.Parameters.AddWithValue("fingerprint", fingerprint);
                insert.Parameters.AddWithValue("now", now);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var persisted = await ReadByPlanDueAsync(
                connection, transaction, entry.PlanId, entry.DueOn, cancellationToken).ConfigureAwait(false);
            if (persisted is null || !string.Equals(persisted.Value.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return Failure<IReadOnlyList<MaintenanceForecastObligation>>(
                    "maintenance.forecast_conflict", "The plan occurrence already has another materialization policy.");
            }

            result.Add(persisted.Value.Obligation);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<MaintenanceForecastObligation>>(result);
    }

    public async Task<MaintenanceForecastObligation?> ClaimNextAsync(
        string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            WITH candidate AS (
                SELECT obligation_id
                FROM {MaintenanceMigrations.Schema}.forecast_materialization
                WHERE completed_at IS NULL AND due_on <= @today
                  AND (state = 1 OR lease_until <= @now)
                ORDER BY due_on, obligation_id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE {MaintenanceMigrations.Schema}.forecast_materialization AS item
            SET state = 2, claimed_by = @worker, lease_until = @lease, attempts = attempts + 1
            FROM candidate
            WHERE item.obligation_id = candidate.obligation_id
            RETURNING item.obligation_id, item.work_order_id, item.plan_id, item.asset_id, item.scope_id,
                      item.plan_revision, item.title, item.due_on, item.assigned_person_id,
                      item.permit_required, item.isolation_required, item.safety_instructions,
                      item.checklist::text, item.state, item.claimed_by, item.lease_until,
                      item.attempts, item.created_at, item.completed_at;
            """, connection, transaction);
        command.Parameters.AddWithValue("today", DateOnly.FromDateTime(now.UtcDateTime));
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("worker", workerId.Trim());
        command.Parameters.AddWithValue("lease", now.Add(leaseDuration));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<bool> CompleteAsync(
        MaintenanceForecastObligationId obligationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        var now = UtcNow();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {MaintenanceMigrations.Schema}.forecast_materialization
            SET state = 3, completed_at = @now
            WHERE obligation_id = @id AND claimed_by = @worker
              AND lease_until > @now AND completed_at IS NULL;
            """, connection, transaction);
        command.Parameters.AddWithValue("id", obligationId.Value);
        command.Parameters.AddWithValue("worker", workerId.Trim());
        command.Parameters.AddWithValue("now", now);
        var completed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return completed;
    }

    public async Task<IReadOnlyList<MaintenanceForecastObligation>> ReadOverdueAsync(
        DateOnly today, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT obligation_id, work_order_id, plan_id, asset_id, scope_id, plan_revision, title, due_on,
                   assigned_person_id, permit_required, isolation_required, safety_instructions, checklist::text,
                   state, claimed_by, lease_until, attempts, created_at, completed_at
            FROM {MaintenanceMigrations.Schema}.forecast_materialization
            WHERE due_on < @today AND completed_at IS NULL
            ORDER BY due_on, obligation_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("today", today);
        var result = new List<MaintenanceForecastObligation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(Read(reader));
        await reader.CloseAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<(MaintenanceForecastObligation Obligation, string Fingerprint)?> ReadByPlanDueAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, MaintenancePlanId planId, DateOnly dueOn,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT obligation_id, work_order_id, plan_id, asset_id, scope_id, plan_revision, title, due_on,
                   assigned_person_id, permit_required, isolation_required, safety_instructions, checklist::text,
                   state, claimed_by, lease_until, attempts, created_at, completed_at, fingerprint
            FROM {MaintenanceMigrations.Schema}.forecast_materialization
            WHERE plan_id = @plan AND due_on = @due;
            """, connection, transaction);
        command.Parameters.AddWithValue("plan", planId.Value);
        command.Parameters.AddWithValue("due", dueOn);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (Read(reader), reader.GetString(19))
            : null;
    }

    private static MaintenanceForecastObligation Read(NpgsqlDataReader reader)
    {
        var checklist = JsonSerializer.Deserialize<ChecklistDto[]>(reader.GetString(12)) ?? [];
        return new(
            MaintenanceForecastObligationId.From(reader.GetGuid(0)), MaintenanceWorkOrderId.From(reader.GetGuid(1)),
            MaintenancePlanId.From(reader.GetGuid(2)), MaintenanceAssetId.From(reader.GetGuid(3)),
            FacilityScopeId.From(reader.GetGuid(4)), RevisionNumber.From(checked((ulong)reader.GetInt64(5))),
            reader.GetString(6), reader.GetFieldValue<DateOnly>(7),
            new MaintenanceMaterializationPolicy(
                PersonId.From(reader.GetGuid(8)),
                new WorkOrderSafetyFields(reader.GetBoolean(9), reader.GetBoolean(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11), null),
                checklist.Select(value => value.ToModel()).ToArray()),
            (MaintenanceMaterializationState)reader.GetInt16(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15), reader.GetInt32(16),
            reader.GetFieldValue<DateTimeOffset>(17),
            reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18));
    }

    private static string Fingerprint(
        MaintenanceForecastEntry entry, FacilityScopeId scopeId, MaintenanceMaterializationPolicy policy) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            PlanId = entry.PlanId.Value, AssetId = entry.AssetId.Value, ScopeId = scopeId.Value,
            Revision = entry.PlanRevision.Value, entry.Title, entry.DueOn,
            Assigned = policy.AssignedPersonId.Value, policy.Safety,
            Checklist = policy.Checklist.OrderBy(value => value.ItemId.Value),
        })))).ToLowerInvariant();

    private async Task SetRoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SET LOCAL ROLE {databaseRole};", connection, transaction);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private DateTimeOffset UtcNow()
    {
        var now = clock.GetUtcNow();
        return now.Offset == TimeSpan.Zero ? now : throw new InvalidOperationException("Maintenance scheduler requires UTC.");
    }

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));

    private sealed record ChecklistDto(Guid ItemId, string Description, bool Mandatory)
    {
        public static ChecklistDto FromModel(NewWorkOrderChecklistItem value) =>
            new(value.ItemId.Value, value.Description.Trim(), value.Mandatory);
        public NewWorkOrderChecklistItem ToModel() =>
            new(WorkOrderChecklistItemId.From(ItemId), Description, Mandatory);
    }
}

public sealed class MaintenanceMaterializer
{
    private readonly MaintenanceSchedulerStore scheduler;
    private readonly MaintenanceWorkStore work;
    private readonly IWallClock clock;

    public MaintenanceMaterializer(MaintenanceSchedulerStore scheduler, MaintenanceWorkStore work, IWallClock clock)
    {
        this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        this.work = work ?? throw new ArgumentNullException(nameof(work));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<Result<MaintenanceMaterializationRun>> RunOnceAsync(
        SessionSnapshot? session, string workerId, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var obligation = await scheduler.ClaimNextAsync(workerId, leaseDuration, cancellationToken).ConfigureAwait(false);
        if (obligation is null) return Result.Success(new MaintenanceMaterializationRun(null, null));
        var authorization = SessionAuthorization.AuthorizeMutation(
            session, MaintenanceWorkPermissions.Manage(obligation.ScopeId), clock);
        if (authorization.IsFailure) return Result.Failure<MaintenanceMaterializationRun>(authorization.Error!);
        var created = await work.CreateWorkOrderAsync(authorization.Value, new CreateWorkOrderFromForecast(
            obligation.WorkOrderId, obligation.ObligationId, obligation.AssetId, obligation.ScopeId,
            obligation.Title, obligation.Policy.AssignedPersonId, obligation.Policy.Safety,
            obligation.Policy.Checklist, $"forecast:{obligation.ObligationId.Value:N}"), cancellationToken).ConfigureAwait(false);
        if (created.IsFailure) return Result.Failure<MaintenanceMaterializationRun>(created.Error!);
        if (!await scheduler.CompleteAsync(obligation.ObligationId, workerId, cancellationToken).ConfigureAwait(false))
        {
            return Failure<MaintenanceMaterializationRun>("maintenance.forecast_lease_lost", "Materialization lease was lost.");
        }

        return Result.Success(new MaintenanceMaterializationRun(obligation, created.Value.Value));
    }

    private static Result<T> Failure<T>(string code, string message) =>
        Result.Failure<T>(new OperationError(ErrorCode.From(code), message));
}
