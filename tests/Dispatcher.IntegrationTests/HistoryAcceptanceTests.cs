using System.Diagnostics;
using Dispatcher.Core;
using Dispatcher.History;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Server;
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

    [Fact]
    public async Task RangePaginationIsStableWhileNewFactsArrive()
    {
        await using var context = await HistoryTestContext.CreateAsync(cluster);
        await context.Store.AcceptAsync(context.Cut(1, 1, 1, 10, Start));
        await context.Store.AcceptAsync(context.Gap(2, 2, 2, 2, "irrecoverable"));
        await context.Store.AcceptAsync(context.Cut(3, 3, 3, 30, Start.AddSeconds(2), DataQuality.Bad, Freshness.Stale));
        var request = context.Range(pageSize: 2);

        var first = await context.Store.QueryRangeAsync(request);
        await context.Store.AcceptAsync(context.Cut(4, 4, 4, 40, Start.AddSeconds(3)));
        var second = await context.Store.QueryRangeAsync(request with { Cursor = first.Value.NextCursor });

        Assert.Equal([1UL, 2UL], first.Value.Records.Select(record => record.Position.Value));
        Assert.NotNull(first.Value.Records[1].Gap);
        Assert.Equal((ulong)3, first.Value.UpperBound.Value);
        var final = Assert.Single(second.Value.Records);
        Assert.Equal((ulong)3, final.Position.Value);
        Assert.Equal(DataQuality.Bad, final.Sample!.Quality);
        Assert.Equal(Freshness.Stale, final.Sample.Freshness);
        Assert.Null(second.Value.NextCursor);
    }

    [Fact]
    public async Task VersionedAggregateIsReproducibleAndCarriesWorstQualityAndGap()
    {
        await using var context = await HistoryTestContext.CreateAsync(cluster);
        await context.Store.AcceptAsync(context.Cut(1, 1, 1, 10, Start.AddSeconds(5)));
        await context.Store.AcceptAsync(context.Cut(
            2, 2, 2, 20, Start.AddSeconds(35), DataQuality.Bad, Freshness.Stale));
        await context.Store.AcceptAsync(context.Gap(3, 3, 3, 3, "source_gap"));
        await context.Store.AcceptAsync(context.Cut(4, 4, 4, 30, Start.AddSeconds(65)));
        var policy = new HistoryResolutionPolicy(1, TimeSpan.FromMinutes(1));

        var first = await context.Store.QueryAggregatesAsync(context.Range(), policy);
        var second = await context.Store.QueryAggregatesAsync(context.Range(), policy);

        Assert.Equal(first.Value.Policy, second.Value.Policy);
        Assert.Equal(first.Value.Buckets.ToArray(), second.Value.Buckets.ToArray());
        Assert.Equal(2, first.Value.Buckets.Count);
        Assert.Equal(2, first.Value.Buckets[0].Count);
        Assert.Equal(15d, first.Value.Buckets[0].Average);
        Assert.Equal(DataQuality.Bad, first.Value.Buckets[0].Quality);
        Assert.Equal(Freshness.Stale, first.Value.Buckets[0].Freshness);
        Assert.True(first.Value.Buckets[0].HasGap);
    }

    [Fact]
    public async Task RetentionDeletesOnlyRecordsInsideExplicitPolicy()
    {
        await using var context = await HistoryTestContext.CreateAsync(cluster);
        await context.Store.AcceptAsync(context.Cut(1, 1, 1, 10, Start.AddHours(-2)));
        await context.Store.AcceptAsync(context.Cut(2, 2, 2, 20, Start));
        await context.Store.AcceptAsync(context.Gap(3, 3, 3, 4, "kept_gap"));

        var retained = await context.Store.ApplyRetentionAsync(
            context.ScopeId,
            new HistoryRetentionPolicy(1, Start.AddHours(-1), new HistoryStreamPosition(3)));
        var page = await context.Store.QueryRangeAsync(context.Range(Start.AddHours(-3), Start.AddHours(1)));

        Assert.Equal(new HistoryRetentionResult(1, 0), retained.Value);
        Assert.Equal([2UL, 3UL], page.Value.Records.Select(record => record.Position.Value));
    }

    [Fact]
    public async Task RangeQueryRequiresHistoryAndExactPointPermissions()
    {
        await using var context = await HistoryTestContext.CreateAsync(cluster);
        await context.Store.AcceptAsync(context.Cut(1, 1, 1, 10, Start));
        var now = Start;
        var deniedSession = new SessionSnapshot(
            SessionId.New(),
            SubjectId.New(),
            PrincipalKind.User,
            now.AddMinutes(-1),
            now.AddHours(1),
            new EffectivePermissions([HistoryPermissions.ReadRange]));
        var reader = new AuthorizedHistoryReader(context.Store, new TestClock(now));

        var denied = await reader.QueryRangeAsync(deniedSession, context.Range(), default);
        var allowedSession = new SessionSnapshot(
            deniedSession.Id,
            deniedSession.SubjectId,
            deniedSession.PrincipalKind,
            deniedSession.IssuedAt,
            deniedSession.ExpiresAt,
            new EffectivePermissions([
                HistoryPermissions.ReadRange,
                RuntimePermissions.ReadPoint(context.PointId),
            ]));
        var allowed = await reader.QueryRangeAsync(
            allowedSession,
            context.Range(),
            default);

        Assert.Equal("permission.denied", denied.Error?.Code.Value);
        Assert.Single(allowed.Value.Records);
    }

    [Fact]
    [Trait("Category", "Workload")]
    public async Task InitialStoreQueryLoadRemainsPageBounded()
    {
        await using var context = await HistoryTestContext.CreateAsync(cluster);
        const int sampleCount = 32;
        for (var index = 1; index <= sampleCount; index++)
        {
            await context.Store.AcceptAsync(context.Cut(
                checked((ulong)index),
                checked((ulong)index),
                checked((ulong)index),
                index,
                Start.AddSeconds(index)));
        }

        var elapsed = Stopwatch.StartNew();
        var cursor = (HistoryQueryCursor?)null;
        var returned = 0;
        do
        {
            var page = await context.Store.QueryRangeAsync(context.Range(pageSize: 8) with { Cursor = cursor });
            Assert.InRange(page.Value.Records.Count, 1, 8);
            returned += page.Value.Records.Count;
            cursor = page.Value.NextCursor;
        }
        while (cursor is not null);

        elapsed.Stop();
        Console.WriteLine($"S16_QUERY_LOAD samples={sampleCount} page_size=8 elapsed_ms={elapsed.Elapsed.TotalMilliseconds:F3}");
        Assert.Equal(sampleCount, returned);
    }

    private static HistoryStreamPosition HistoryStreamPositionOne => new(1);

    private sealed class HistoryTestContext : IAsyncDisposable
    {
        private static readonly SourceId TestSourceId = Dispatcher.Core.SourceId.From(
            Guid.Parse("b2000000-0000-7000-8000-000000000001"));
        private static readonly PointId TestPointId = Dispatcher.Semantics.PointId.From(
            Guid.Parse("b3000000-0000-7000-8000-000000000001"));

        private HistoryTestContext(TestDatabase database, NpgsqlDataSource dataSource)
        {
            Database = database;
            DataSource = dataSource;
            ScopeId = RuntimeScopeId.From(Guid.Parse("b1000000-0000-7000-8000-000000000001"));
            SourceId = TestSourceId;
            PointId = TestPointId;
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

        public SourceId SourceId { get; }

        public PointId PointId { get; }

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
            new(
                DataSource,
                PostgreSqlClusterFixture.OwnerBRole,
                new TestClock(Start),
                new HistoryQueryLimits(maxPageSize: 100, maxAggregateBuckets: 100),
                hook);

        public RuntimeSourceObligation Cut(
            ulong runtimePosition,
            ulong scheduleSequence,
            ulong sourcePosition,
            long value,
            DateTimeOffset sourceTimestamp,
            DataQuality quality = DataQuality.Good,
            Freshness freshness = Freshness.Fresh)
        {
            var observation = new SourceObservation(
                ScopeId,
                TestSourceId,
                TestPointId,
                new OwnerPosition<SourceObservation>(sourcePosition),
                TypedValue.From(value),
                Unit.FromSymbol("kW"),
                quality,
                freshness,
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

        public HistoryRangeRequest Range(int pageSize = 100) =>
            Range(Start.AddHours(-3), Start.AddHours(3), pageSize);

        public HistoryRangeRequest Range(
            DateTimeOffset from,
            DateTimeOffset to,
            int pageSize = 100) =>
            new(ScopeId, TestSourceId, TestPointId, from, to, pageSize);

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
