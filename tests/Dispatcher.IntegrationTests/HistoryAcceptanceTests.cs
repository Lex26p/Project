using Dispatcher.Core;
using Dispatcher.History;
using Dispatcher.Persistence;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class HistoryAcceptanceTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 19, 18, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public HistoryAcceptanceTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task OutOfOrderAcceptanceKeepsIndependentStreamAndAdvancesContiguousCheckpoint()
    {
        await using var context = await HistoryTestContext.CreateAsync(cluster);
        var second = await context.Store.AcceptAsync(context.Cut(2, 2, 2, 20, Start.AddSeconds(2)));

        Assert.True(second.IsSuccess);
        Assert.Equal(HistoryStreamPositionOne, Assert.Single(second.Value.Samples).HistoryStreamPosition);
        Assert.Equal(new RuntimeFactPosition(2), second.Value.RuntimeFactPosition);
        Assert.Equal(new RuntimeFactPosition(0), second.Value.Checkpoint.RuntimeFactPosition);

        var first = await context.Store.AcceptAsync(context.Cut(1, 1, 1, 10, Start.AddSeconds(1)));

        var late = Assert.Single(first.Value.Samples);
        Assert.Equal(new HistoryStreamPosition(2), late.HistoryStreamPosition);
        Assert.Equal(new RuntimeFactPosition(1), late.RuntimeFactPosition);
        Assert.True(late.IsLate);
        Assert.True(late.IsOutOfOrder);
        Assert.Equal(new RuntimeFactPosition(2), first.Value.Checkpoint.RuntimeFactPosition);
        Assert.Equal(new HistoryStreamPosition(2), first.Value.Checkpoint.HistoryStreamPosition);
        Assert.Equal(first.Value.Checkpoint, await context.Store.LoadRecoveryCheckpointAsync(context.ScopeId));
    }

    [Fact]
    public async Task CrashAfterCommitReplaysAsDuplicateWithoutSecondSample()
    {
        await using var context = await HistoryTestContext.CreateAsync(cluster);
        var crashingStore = context.CreateStore(
            new HistoryIngestCommitHook(
                (_, _) => throw new InvalidOperationException("simulated crash after history commit")));
        var fact = context.Cut(1, 1, 1, 10, Start);

        await Assert.ThrowsAsync<InvalidOperationException>(() => crashingStore.AcceptAsync(fact));

        var replay = await context.Store.AcceptAsync(fact);
        var repeatedReplay = await context.Store.AcceptAsync(fact);
        Assert.Equal(HistoryIngestStatus.Duplicate, replay.Value.Status);
        Assert.Equal(HistoryIngestStatus.Duplicate, repeatedReplay.Value.Status);
        Assert.Equal(HistoryStreamPositionOne, Assert.Single(replay.Value.Samples).HistoryStreamPosition);
        Assert.Equal(new RuntimeFactPosition(1), replay.Value.Checkpoint.RuntimeFactPosition);
    }

    [Fact]
    public async Task SameRuntimePositionWithDifferentContentFailsConflict()
    {
        await using var context = await HistoryTestContext.CreateAsync(cluster);
        var original = context.Cut(1, 1, 1, 10, Start);
        Assert.True((await context.Store.AcceptAsync(original)).IsSuccess);

        var conflict = await context.Store.AcceptAsync(context.Cut(1, 1, 1, 11, Start));

        Assert.Equal("history.ingest_conflict", conflict.Error?.Code.Value);
        var replay = await context.Store.AcceptAsync(original);
        Assert.Equal(HistoryIngestStatus.Duplicate, replay.Value.Status);
        Assert.Equal(10, Assert.Single(replay.Value.Samples).Value.Value);
    }

    [Fact]
    public async Task IrrecoverableSourceIntervalIsPersistedAsGap()
    {
        await using var context = await HistoryTestContext.CreateAsync(cluster);
        var fact = context.Gap(1, 1, 8, 12, "source_evidence_unavailable");

        var accepted = await context.Store.AcceptAsync(fact);
        var replay = await context.Store.AcceptAsync(fact);

        Assert.Equal(HistoryIngestStatus.Accepted, accepted.Value.Status);
        Assert.Empty(accepted.Value.Samples);
        Assert.Equal((ulong)8, accepted.Value.Gap!.FirstSourcePosition);
        Assert.Equal((ulong)12, accepted.Value.Gap.LastSourcePosition);
        Assert.Equal("source_evidence_unavailable", accepted.Value.Gap.Reason);
        Assert.Equal(HistoryIngestStatus.Duplicate, replay.Value.Status);
        Assert.Equal(accepted.Value.Gap, replay.Value.Gap);
        Assert.Equal(new RuntimeFactPosition(1), replay.Value.Checkpoint.RuntimeFactPosition);
    }

    private static HistoryStreamPosition HistoryStreamPositionOne => new(1);

    private sealed class HistoryTestContext : IAsyncDisposable
    {
        private static readonly SourceId SourceId = Dispatcher.Core.SourceId.From(
            Guid.Parse("b2000000-0000-7000-8000-000000000001"));
        private static readonly PointId PointId = Dispatcher.Semantics.PointId.From(
            Guid.Parse("b3000000-0000-7000-8000-000000000001"));

        private HistoryTestContext(TestDatabase database, NpgsqlDataSource dataSource)
        {
            Database = database;
            DataSource = dataSource;
            ScopeId = RuntimeScopeId.From(Guid.Parse("b1000000-0000-7000-8000-000000000001"));
            Binding = new SourceBinding(
                ScopeId,
                SourceId,
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1));
            Store = CreateStore();
        }

        public TestDatabase Database { get; }

        public NpgsqlDataSource DataSource { get; }

        public RuntimeScopeId ScopeId { get; }

        public SourceBinding Binding { get; }

        public HistoryStore Store { get; }

        public static async Task<HistoryTestContext> CreateAsync(PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                HistoryMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));
            return new HistoryTestContext(database, dataSource);
        }

        public HistoryStore CreateStore(HistoryIngestCommitHook? hook = null) =>
            new(DataSource, PostgreSqlClusterFixture.OwnerBRole, new TestClock(Start), hook);

        public RuntimeSourceObligation Cut(
            ulong runtimePosition,
            ulong scheduleSequence,
            ulong sourcePosition,
            long value,
            DateTimeOffset sourceTimestamp)
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
                SourceTimestamp.FromUtc(sourceTimestamp));
            var cut = RuntimeCut.Normalize(Binding, scheduleSequence, [observation]).Value;
            return new RuntimeSourceObligation(
                ScopeId,
                new OwnerPosition<RuntimeSourceObligation>(runtimePosition),
                RuntimeFactClass.SourceCut,
                RuntimeFactProtection.Protected,
                Binding,
                scheduleSequence,
                cut,
                null,
                Start,
                null);
        }

        public RuntimeSourceObligation Gap(
            ulong runtimePosition,
            ulong scheduleSequence,
            ulong firstSourcePosition,
            ulong lastSourcePosition,
            string reason)
        {
            var gap = new RuntimeSourceGap(
                Binding,
                scheduleSequence,
                firstSourcePosition,
                lastSourcePosition,
                reason);
            return new RuntimeSourceObligation(
                ScopeId,
                new OwnerPosition<RuntimeSourceObligation>(runtimePosition),
                RuntimeFactClass.SourceGap,
                RuntimeFactProtection.Protected,
                Binding,
                scheduleSequence,
                null,
                gap,
                Start,
                null);
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private sealed class TestClock : IWallClock
    {
        private readonly DateTimeOffset now;

        public TestClock(DateTimeOffset now)
        {
            this.now = now;
        }

        public DateTimeOffset GetUtcNow() => now;
    }
}
