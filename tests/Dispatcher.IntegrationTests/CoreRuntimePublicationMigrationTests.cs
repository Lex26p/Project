using Dispatcher.Core;
using Dispatcher.Persistence;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class CoreRuntimePublicationMigrationTests
{
    private static readonly Guid ScopeId =
        Guid.Parse("ba000000-0000-0000-0000-000000000001");
    private static readonly Guid SourceId =
        Guid.Parse("bb000000-0000-0000-0000-000000000001");
    private static readonly Guid PointId =
        Guid.Parse("bc000000-0000-0000-0000-000000000001");
    private readonly PostgreSqlClusterFixture cluster;

    public CoreRuntimePublicationMigrationTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task MigrationCreatesBoundedPublicationBoundaryAndSerializesPendingDelivery()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            CoreRuntimeMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));

        await CreateTwoObligationsAndFirstDeliveryAsync(dataSource);

        var conflict = await Assert.ThrowsAsync<PostgresException>(
            () => InsertPendingDeliveryAsync(dataSource, obligationPosition: 2));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, conflict.SqlState);

        await CompleteFirstDeliveryAsync(dataSource);
        await InsertPendingDeliveryAsync(dataSource, obligationPosition: 2);
        await PublishCurrentAndPruneDeltaAsync(dataSource);

        var counts = await ReadCountsAsync(dataSource);
        Assert.Equal(1L, counts.UnfinishedDeliveries);
        Assert.Equal(1L, counts.PublishedScopes);
        Assert.Equal(1L, counts.PublishedCurrent);
        Assert.Equal(0L, counts.PublishedDeltas);
    }

    private static async Task CreateTwoObligationsAndFirstDeliveryAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetRoleAsync(connection, transaction);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {CoreRuntimeMigrations.Schema}.scope_state
                (scope_id, next_obligation_position)
            VALUES (@scope_id, 3);

            INSERT INTO {CoreRuntimeMigrations.Schema}.source_obligation
                (scope_id, obligation_position, fact_class, protection, source_id,
                 binding_generation, session_generation, schedule_sequence,
                 first_source_position, last_source_position, payload, recorded_at)
            VALUES
                (@scope_id, 1, 2, 2, @source_id, 1, 1, 1, 1, 1, jsonb_build_object(), now()),
                (@scope_id, 2, 2, 2, @source_id, 1, 1, 2, 2, 2, jsonb_build_object(), now());

            INSERT INTO {CoreRuntimeMigrations.Schema}.processing_delivery
                (scope_id, obligation_position, fact_class, stage, post_cut_acceptance,
                 created_at, updated_at)
            VALUES (@scope_id, 1, 2, 1, jsonb_build_object(), now(), now());
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", ScopeId);
        command.Parameters.AddWithValue("source_id", SourceId);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task InsertPendingDeliveryAsync(
        NpgsqlDataSource dataSource,
        long obligationPosition)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetRoleAsync(connection, transaction);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {CoreRuntimeMigrations.Schema}.processing_delivery
                (scope_id, obligation_position, fact_class, stage, post_cut_acceptance,
                 created_at, updated_at)
            VALUES (@scope_id, @position, 2, 1, jsonb_build_object(), now(), now());
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", ScopeId);
        command.Parameters.AddWithValue("position", obligationPosition);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task CompleteFirstDeliveryAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetRoleAsync(connection, transaction);
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {CoreRuntimeMigrations.Schema}.processing_delivery
            SET stage = 3,
                history_state = 2,
                alarm_state = 2,
                event_state = 2,
                downstream_completed_at = now(),
                published_at = now(),
                updated_at = now()
            WHERE scope_id = @scope_id AND obligation_position = 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", ScopeId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();
    }

    private static async Task PublishCurrentAndPruneDeltaAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetRoleAsync(connection, transaction);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {CoreRuntimeMigrations.Schema}.published_scope
                (scope_id, completed_obligation_position, current_position,
                 earliest_delta_position, protected_continuity, ready, heartbeat_at, published_at)
            VALUES (@scope_id, 1, 1, 1, true, true, now(), now());

            INSERT INTO {CoreRuntimeMigrations.Schema}.published_current
                (scope_id, point_id, source_id, binding_generation, session_generation,
                 source_position, current_position, value, unit, quality, freshness,
                 source_timestamp, receive_timestamp, processed_timestamp)
            VALUES
                (@scope_id, @point_id, @source_id, 1, 1, 1, 1, 42, 'kW', 1, 1,
                 now(), now(), now());

            INSERT INTO {CoreRuntimeMigrations.Schema}.published_delta
                (scope_id, current_position, point_id, source_id,
                 binding_generation, session_generation, source_position,
                 value, unit, quality, freshness,
                 source_timestamp, receive_timestamp, processed_timestamp)
            VALUES
                (@scope_id, 1, @point_id, @source_id, 1, 1, 1,
                 42, 'kW', 1, 1, now(), now(), now());

            DELETE FROM {CoreRuntimeMigrations.Schema}.published_delta
            WHERE scope_id = @scope_id AND current_position = 1;

            UPDATE {CoreRuntimeMigrations.Schema}.published_scope
            SET earliest_delta_position = 2
            WHERE scope_id = @scope_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", ScopeId);
        command.Parameters.AddWithValue("source_id", SourceId);
        command.Parameters.AddWithValue("point_id", PointId);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task<PublicationCounts> ReadCountsAsync(NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetRoleAsync(connection, transaction);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT
                (SELECT count(*) FROM {CoreRuntimeMigrations.Schema}.processing_delivery WHERE stage < 3),
                (SELECT count(*) FROM {CoreRuntimeMigrations.Schema}.published_scope),
                (SELECT count(*) FROM {CoreRuntimeMigrations.Schema}.published_current),
                (SELECT count(*) FROM {CoreRuntimeMigrations.Schema}.published_delta);
            """,
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new PublicationCounts(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3));
    }

    private sealed record PublicationCounts(
        long UnfinishedDeliveries,
        long PublishedScopes,
        long PublishedCurrent,
        long PublishedDeltas);

    private static async Task SetRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            $"SET LOCAL ROLE \"{PostgreSqlClusterFixture.OwnerBRole}\";",
            connection,
            transaction);
        await command.ExecuteNonQueryAsync();
    }
}
