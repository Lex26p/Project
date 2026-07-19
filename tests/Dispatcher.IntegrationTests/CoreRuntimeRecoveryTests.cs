using Dispatcher.Core;
using Dispatcher.Persistence;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class CoreRuntimeRecoveryTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 19, 14, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public CoreRuntimeRecoveryTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task CheckpointRestoresCurrentAndLifecycleDrainsInOrder()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var first = context.CreateHost(2);
        using (first.Host)
        {
            Assert.Equal(
                "runtime.required_evidence_unavailable",
                first.Host.EnsureUserMutationReady().Error?.Code.Value);
            Assert.True((await first.Host.StartAsync()).IsSuccess);
            Assert.True(first.Host.ActivateBinding(context.Binding).IsSuccess);
            Assert.True(first.Host.EnsureUserMutationReady().IsSuccess);

            Assert.Equal(
                RuntimeIngressStatus.Queued,
                (await first.Host.EnqueueAsync(context.Cut(1, 1, 10))).Value.Status);
            Assert.Empty(first.Runtime.GetSnapshot().Entries);
            Assert.Equal(1L, await context.Store.CountPendingAsync(context.ScopeId));
            await context.AssertProtectedObligationIsImmutableAsync();
            Assert.True((await first.Host.ProcessNextAsync()).Value);
            Assert.Equal(0L, await context.Store.CountPendingAsync(context.ScopeId));
            Assert.Equal(10, Assert.Single(first.Runtime.GetSnapshot().Entries).Value.Value);
            Assert.True((await first.Host.DrainAsync()).IsSuccess);
            Assert.Equal(RuntimeHostState.Stopped, first.Host.GetReadiness().State);
            Assert.False(first.Host.GetReadiness().AdmissionOpen);
            Assert.Equal(
                "runtime.required_evidence_unavailable",
                first.Host.EnsureUserMutationReady().Error?.Code.Value);
        }

        var restarted = context.CreateHost(2);
        using (restarted.Host)
        {
            Assert.True((await restarted.Host.StartAsync()).IsSuccess);
            var snapshot = restarted.Runtime.GetSnapshot();
            var liveness = restarted.Runtime.GetLivenessSnapshot();
            Assert.Equal((ulong)1, snapshot.Position.Value);
            Assert.Equal(10, Assert.Single(snapshot.Entries).Value.Value);
            Assert.Equal((ulong)1, liveness.Position.Value);
            Assert.Equal((ulong)1, Assert.Single(liveness.Sources).LastSourcePosition);
            Assert.True(restarted.Host.GetReadiness().RecoveryComplete);
            Assert.True(restarted.Host.EnsureUserMutationReady().IsSuccess);
        }
    }

    [Fact]
    public async Task CrashAfterProtectedAdmissionReplaysPendingObligation()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var crashing = context.CreateHost(
            1,
            new RuntimeObligationCommitHook(
                (_, _) => throw new InvalidOperationException("simulated crash after durable admission")));
        using (crashing.Host)
        {
            Assert.True((await crashing.Host.StartAsync()).IsSuccess);
            Assert.True(crashing.Host.ActivateBinding(context.Binding).IsSuccess);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => crashing.Host.EnqueueAsync(context.Cut(1, 1, 25)));
            Assert.Empty(crashing.Runtime.GetSnapshot().Entries);
            Assert.Equal(1L, await context.Store.CountPendingAsync(context.ScopeId));
        }

        var recovered = context.CreateHost(1);
        using (recovered.Host)
        {
            Assert.True((await recovered.Host.StartAsync()).IsSuccess);
            Assert.Equal(25, Assert.Single(recovered.Runtime.GetSnapshot().Entries).Value.Value);
            Assert.Equal(0L, await context.Store.CountPendingAsync(context.ScopeId));
            Assert.True(recovered.Host.GetReadiness().ProtectedContinuity);
            Assert.True(recovered.Host.EnsureUserMutationReady().IsSuccess);
        }
    }

    [Fact]
    public async Task CapacityExhaustionRecordsVisibleGapAndKeepsReadOnlyCurrent()
    {
        await using var context = await RuntimeTestContext.CreateAsync(cluster);
        var running = context.CreateHost(1);
        using (running.Host)
        {
            Assert.True((await running.Host.StartAsync()).IsSuccess);
            Assert.True(running.Host.ActivateBinding(context.Binding).IsSuccess);
            Assert.Equal(
                RuntimeIngressStatus.Queued,
                (await running.Host.EnqueueAsync(context.Cut(1, 1, 10))).Value.Status);
            var overflow = await running.Host.EnqueueAsync(context.Cut(2, 2, 20));
            Assert.Equal(RuntimeIngressStatus.GapRecorded, overflow.Value.Status);
            Assert.Equal(RuntimeFactProtection.Protected, overflow.Value.GapObligation!.Protection);
            Assert.Equal(RuntimeFactClass.SourceGap, overflow.Value.GapObligation.FactClass);
            Assert.Equal(1, running.Host.GetReadiness().QueuedCuts);
            Assert.False(running.Host.GetReadiness().ProtectedContinuity);
            Assert.False(running.Host.GetReadiness().AdmissionOpen);
            Assert.Equal(
                "runtime.required_evidence_unavailable",
                running.Host.EnsureUserMutationReady().Error?.Code.Value);

            var gap = Assert.Single(await context.Store.ReadGapsAsync(context.ScopeId));
            Assert.Equal((ulong)2, gap.Gap!.FirstSourcePosition);
            Assert.Equal("bounded_ingress_capacity", gap.Gap.Reason);
            Assert.Empty(running.Runtime.GetSnapshot().Entries);
            Assert.Equal(2L, await context.Store.CountPendingAsync(context.ScopeId));
        }

        var restarted = context.CreateHost(1);
        using (restarted.Host)
        {
            Assert.True((await restarted.Host.StartAsync()).IsSuccess);
            Assert.Equal(10, Assert.Single(restarted.Runtime.GetSnapshot().Entries).Value.Value);
            Assert.Equal(0L, await context.Store.CountPendingAsync(context.ScopeId));
            Assert.False(restarted.Host.GetReadiness().ProtectedContinuity);
            Assert.Equal(
                "runtime.required_evidence_unavailable",
                restarted.Host.EnsureUserMutationReady().Error?.Code.Value);
            Assert.Single(await context.Store.ReadGapsAsync(context.ScopeId));
        }
    }

    private sealed class RuntimeTestContext : IAsyncDisposable
    {
        private static readonly SourceId SourceId = Dispatcher.Core.SourceId.From(
            Guid.Parse("95000000-0000-0000-0000-000000000001"));
        private static readonly PointId PointId = Dispatcher.Semantics.PointId.From(
            Guid.Parse("96000000-0000-0000-0000-000000000001"));

        private RuntimeTestContext(TestDatabase database, NpgsqlDataSource dataSource, TestClock clock)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            ScopeId = RuntimeScopeId.From(Guid.Parse("94000000-0000-0000-0000-000000000001"));
            Binding = new SourceBinding(
                ScopeId,
                SourceId,
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1));
            Store = new CoreRuntimeStore(DataSource, PostgreSqlClusterFixture.OwnerBRole, Clock);
        }

        public TestDatabase Database { get; }
        public NpgsqlDataSource DataSource { get; }
        public TestClock Clock { get; }
        public RuntimeScopeId ScopeId { get; }
        public SourceBinding Binding { get; }
        public CoreRuntimeStore Store { get; }

        public static async Task<RuntimeTestContext> CreateAsync(PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                CoreRuntimeMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
            return new RuntimeTestContext(database, dataSource, new TestClock(Start));
        }

        public HostBundle CreateHost(int capacity, RuntimeObligationCommitHook? hook = null)
        {
            var runtime = new CoreRuntime(
                ScopeId,
                Clock,
                Clock,
                new RuntimeCurrentLimits(maxPoints: 8, retainedChangeCapacity: 64));
            return new HostBundle(
                runtime,
                new CoreRuntimeHost(
                    ScopeId,
                    runtime,
                    Store,
                    new RuntimeIngressLimits(capacity),
                    hook));
        }

        public RuntimeCut Cut(ulong scheduleSequence, ulong sourcePosition, long value)
        {
            var observation = new SourceObservation(
                ScopeId,
                SourceId,
                PointId,
                new OwnerPosition<SourceObservation>(sourcePosition),
                TypedValue.From(value),
                Unit.FromSymbol("kW"),
                DataQuality.Good,
                Freshness.Fresh,
                SourceTimestamp.FromUtc(Start.AddSeconds(checked((long)sourcePosition))));
            return RuntimeCut.Normalize(Binding, scheduleSequence, [observation]).Value;
        }

        public async Task AssertProtectedObligationIsImmutableAsync()
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
                UPDATE {CoreRuntimeMigrations.Schema}.source_obligation
                SET gap_reason = 'tampered' WHERE scope_id = @scope_id AND obligation_position = 1;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("scope_id", ScopeId.Value);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("immutable", exception.MessageText, StringComparison.Ordinal);
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private sealed record HostBundle(CoreRuntime Runtime, CoreRuntimeHost Host);

    public sealed class TestClock : IWallClock, IMonotonicClock
    {
        private readonly DateTimeOffset now;

        public TestClock(DateTimeOffset now)
        {
            this.now = now;
        }

        public DateTimeOffset GetUtcNow() => now;

        public MonotonicTimestamp GetTimestamp() => new(0);

        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) =>
            TimeSpan.FromTicks(checked(finish.Value - start.Value));
    }
}
