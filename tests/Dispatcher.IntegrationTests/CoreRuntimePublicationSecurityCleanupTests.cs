using Dispatcher.Core;
using Dispatcher.DatabaseMigrator;
using Dispatcher.Persistence;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class CoreRuntimePublicationSecurityCleanupTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 27, 14, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public CoreRuntimePublicationSecurityCleanupTests(
        PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }


    [Fact]
    public void MigratorRequiresPublishedReadRoleMapping()
    {
        var variables = new Dictionary<string, string?>
        {
            [MigrationEnvironmentVariables.ConnectionString] =
                "Host=localhost;Database=dispatcher;Username=migrator",
        };
        foreach (var registration in MigrationCatalog.Registrations)
        {
            variables[MigrationEnvironmentVariables.GetRoleVariableName(
                registration.Owner)] = "migration_test_role";
        }

        var missing = MigrationConfigurationParser.Parse(
            variables,
            MigrationCatalog.Registrations);
        Assert.False(missing.IsValid);
        Assert.Contains(
            missing.Errors,
            error => error.Contains(
                MigrationEnvironmentVariables.GetRoleVariableName(
                    CoreRuntimeMigrations.PublishedReadRoleKey),
                StringComparison.Ordinal));

        variables[MigrationEnvironmentVariables.GetRoleVariableName(
            CoreRuntimeMigrations.PublishedReadRoleKey)] =
            "core_runtime_published_reader";
        var valid = MigrationConfigurationParser.Parse(
            variables,
            MigrationCatalog.Registrations);

        Assert.True(valid.IsValid);
        Assert.Equal(
            "core_runtime_published_reader",
            valid.Configuration!.GetDatabaseRole(
                CoreRuntimeMigrations.PublishedReadRoleKey));
    }

    [Fact]
    public async Task PublishedReadRoleCannotSeeRecoveryInternals()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var runtime = context.CreateRuntime();
        Assert.True(runtime.ActivateBinding(context.Binding).IsSuccess);
        await context.PublishAsync(runtime, 1, 1, 42);

        var snapshot = await context.Reader.ReadSnapshotAsync(context.ScopeId);
        Assert.True(snapshot.Readiness.CanServeCurrent);
        Assert.Equal(42L, Assert.Single(snapshot.Entries).Value.Value);

        Assert.Equal(
            1L,
            await context.ExecuteReadScalarAsync(
                $"SELECT count(*) FROM {CoreRuntimeMigrations.Schema}.published_current;"));

        var sourceDenied = await Assert.ThrowsAsync<PostgresException>(
            () => context.ExecuteReadScalarAsync(
                $"SELECT count(*) FROM {CoreRuntimeMigrations.Schema}.source_obligation;"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, sourceDenied.SqlState);

        var deliveryDenied = await Assert.ThrowsAsync<PostgresException>(
            () => context.ExecuteReadScalarAsync(
                $"SELECT count(*) FROM {CoreRuntimeMigrations.Schema}.processing_delivery;"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deliveryDenied.SqlState);

        var writeDenied = await Assert.ThrowsAsync<PostgresException>(
            () => context.ExecuteReadScalarAsync(
                $"""
                DELETE FROM {CoreRuntimeMigrations.Schema}.published_current
                WHERE scope_id = '{context.ScopeId.Value}';
                SELECT 0::bigint;
                """));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, writeDenied.SqlState);
    }

    [Fact]
    public async Task CleanupIsBoundedAndPreservesUnfinishedDelivery()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var runtime = context.CreateRuntime();
        Assert.True(runtime.ActivateBinding(context.Binding).IsSuccess);

        await context.PublishAsync(runtime, 1, 1, 10);
        context.Clock.Advance(TimeSpan.FromMinutes(2));
        await context.PublishAsync(runtime, 2, 2, 20);
        var unfinished = await context.CreatePendingAsync(runtime, 3, 3, 30);

        var firstCleanup = await context.Store.CleanupCompletedDeliveriesAsync(
            context.ScopeId,
            safetyWindow: TimeSpan.FromMinutes(1),
            maxDeleteCount: 10);
        Assert.Equal(1, firstCleanup.DeliveriesDeleted);
        Assert.Equal(1, firstCleanup.ObligationsDeleted);

        var afterFirst = await context.ReadJournalCountsAsync();
        Assert.Equal(2L, afterFirst.DeliveryCount);
        Assert.Equal(2L, afterFirst.ObligationCount);
        Assert.Equal(1L, afterFirst.UnfinishedCount);
        Assert.NotNull(await context.Store.LoadPendingDeliveryAsync(context.ScopeId));

        context.Clock.Advance(TimeSpan.FromMinutes(2));
        var secondCleanup = await context.Store.CleanupCompletedDeliveriesAsync(
            context.ScopeId,
            safetyWindow: TimeSpan.FromMinutes(1),
            maxDeleteCount: 1);
        Assert.Equal(1, secondCleanup.DeliveriesDeleted);
        Assert.Equal(1, secondCleanup.ObligationsDeleted);

        var final = await context.ReadJournalCountsAsync();
        Assert.Equal(1L, final.DeliveryCount);
        Assert.Equal(1L, final.ObligationCount);
        Assert.Equal(1L, final.UnfinishedCount);
        var pending = await context.Store.LoadPendingDeliveryAsync(context.ScopeId);
        Assert.NotNull(pending);
        Assert.Equal(unfinished.Position, pending.ObligationPosition);

        var snapshot = await context.Reader.ReadSnapshotAsync(context.ScopeId);
        Assert.Equal(20L, Assert.Single(snapshot.Entries).Value.Value);
    }

    private sealed class RuntimeTestContext : IAsyncDisposable
    {
        private static readonly SourceId SourceId = Dispatcher.Core.SourceId.From(
            Guid.Parse("c9000000-0000-0000-0000-000000000001"));
        private static readonly PointId PointId = Dispatcher.Semantics.PointId.From(
            Guid.Parse("ca000000-0000-0000-0000-000000000001"));

        private RuntimeTestContext(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            MutableClock clock,
            string readRole)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ReadRole = readRole;
            ScopeId = RuntimeScopeId.From(
                Guid.Parse("cb000000-0000-0000-0000-000000000001"));
            Binding = new SourceBinding(
                ScopeId,
                SourceId,
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1));
            Store = new CoreRuntimeStore(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                Clock);
            Reader = new CoreRuntimePublishedReader(
                DataSource,
                ReadRole,
                new PublishedCurrentReadLimits(64, 64));
        }

        public TestDatabase Database { get; }

        public NpgsqlDataSource DataSource { get; }

        public MutableClock Clock { get; }

        public string ReadRole { get; }

        public RuntimeScopeId ScopeId { get; }

        public SourceBinding Binding { get; }

        public CoreRuntimeStore Store { get; }

        public CoreRuntimePublishedReader Reader { get; }

        public static async Task<RuntimeTestContext> CreateAsync(
            PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            var readRole = $"core_runtime_read_{Guid.NewGuid():N}"[..40];

            await using (var connection = await dataSource.OpenConnectionAsync())
            {
                await using var createRole = new NpgsqlCommand(
                    $"CREATE ROLE \"{readRole}\" NOLOGIN;",
                    connection);
                await createRole.ExecuteNonQueryAsync();
            }

            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                CoreRuntimeMigrations.CreatePlan(
                    PostgreSqlClusterFixture.OwnerBRole,
                    readRole));
            return new RuntimeTestContext(
                database,
                dataSource,
                new MutableClock(Start),
                readRole);
        }

        public CoreRuntime CreateRuntime() => new(
            ScopeId,
            Clock,
            Clock,
            new RuntimeCurrentLimits(
                maxPoints: 8,
                retainedChangeCapacity: 8));

        public async Task PublishAsync(
            CoreRuntime runtime,
            ulong scheduleSequence,
            ulong sourcePosition,
            long value)
        {
            var obligation = await CreatePendingAsync(
                runtime,
                scheduleSequence,
                sourcePosition,
                value);
            Assert.True((await Store.CompleteDownstreamAsync(
                ScopeId,
                obligation.Position)).IsSuccess);
            Assert.True((await Store.PublishCompletedDeliveryAsync(
                ScopeId,
                obligation.Position,
                retainedDeltaCapacity: 8,
                ready: true)).IsSuccess);
        }

        public async Task<RuntimeSourceObligation> CreatePendingAsync(
            CoreRuntime runtime,
            ulong scheduleSequence,
            ulong sourcePosition,
            long value)
        {
            var cut = Cut(scheduleSequence, sourcePosition, value);
            var obligation = await Store.AppendCutAsync(cut);
            var acceptance = runtime.Apply(cut).Value;
            var saved = await Store.SaveCheckpointWithPendingDeliveryAsync(
                runtime.CaptureCheckpoint(),
                obligation,
                acceptance,
                protectedContinuity: true);
            Assert.True(saved.IsSuccess);
            return obligation;
        }

        public RuntimeCut Cut(
            ulong scheduleSequence,
            ulong sourcePosition,
            long value)
        {
            var observation = new SourceObservation(
                ScopeId,
                SourceId,
                PointId,
                new OwnerPosition<SourceObservation>(sourcePosition),
                TypedValue.From((decimal)value),
                Unit.FromSymbol("kW"),
                DataQuality.Good,
                Freshness.Fresh,
                SourceTimestamp.FromUtc(
                    Clock.GetUtcNow().AddSeconds(1)));
            return RuntimeCut.Normalize(
                Binding,
                scheduleSequence,
                [observation]).Value;
        }

        public async Task<long> ExecuteReadScalarAsync(string sql)
        {
            await using var connection = await DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var setRole = new NpgsqlCommand(
                $"SET LOCAL ROLE \"{ReadRole}\";",
                connection,
                transaction);
            await setRole.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(
                sql,
                connection,
                transaction);
            var value = await command.ExecuteScalarAsync();
            await transaction.CommitAsync();
            return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<JournalCounts> ReadJournalCountsAsync()
        {
            await using var connection = await DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var setRole = new NpgsqlCommand(
                $"SET LOCAL ROLE \"{PostgreSqlClusterFixture.OwnerBRole}\";",
                connection,
                transaction);
            await setRole.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(
                $"""
                SELECT
                    (SELECT count(*)
                     FROM {CoreRuntimeMigrations.Schema}.processing_delivery
                     WHERE scope_id = @scope_id),
                    (SELECT count(*)
                     FROM {CoreRuntimeMigrations.Schema}.source_obligation
                     WHERE scope_id = @scope_id),
                    (SELECT count(*)
                     FROM {CoreRuntimeMigrations.Schema}.processing_delivery
                     WHERE scope_id = @scope_id AND stage < 3);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("scope_id", ScopeId.Value);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return new JournalCounts(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2));
        }

        public async ValueTask DisposeAsync()
        {
            await using (var connection = await DataSource.OpenConnectionAsync())
            {
                await using var dropRole = new NpgsqlCommand(
                    $"DROP OWNED BY \"{ReadRole}\"; DROP ROLE \"{ReadRole}\";",
                    connection);
                await dropRole.ExecuteNonQueryAsync();
            }

            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private sealed record JournalCounts(
        long DeliveryCount,
        long ObligationCount,
        long UnfinishedCount);

    private sealed class MutableClock : IWallClock, IMonotonicClock
    {
        private DateTimeOffset now;

        public MutableClock(DateTimeOffset now)
        {
            this.now = now;
        }

        public DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan interval)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
                interval,
                TimeSpan.Zero);
            now += interval;
        }

        public MonotonicTimestamp GetTimestamp() => new(now.UtcTicks);

        public TimeSpan GetElapsedTime(
            MonotonicTimestamp start,
            MonotonicTimestamp finish) =>
            TimeSpan.FromTicks(checked(finish.Value - start.Value));
    }
}
