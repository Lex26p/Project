using Dispatcher.Core;
using Dispatcher.Semantics;
using Dispatcher.Simulator;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class SimulatorWalkingSkeletonTests
{
    private static readonly RuntimeScopeId ScopeId = RuntimeScopeId.From(
        Guid.Parse("11111111-1111-7111-8111-111111111111"));
    private static readonly SourceId SourceId = SourceId.From(
        Guid.Parse("22222222-2222-7222-8222-222222222222"));
    private static readonly PointId PointA = PointId.From(
        Guid.Parse("33333333-3333-7333-8333-333333333333"));
    private static readonly PointId PointB = PointId.From(
        Guid.Parse("44444444-4444-7444-8444-444444444444"));
    private static readonly DateTimeOffset Start = new(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameSeedConfigAndClocksProduceCanonicalGoldenTrace()
    {
        var first = RunTrace();
        var second = RunTrace();
        var goldenPath = Path.Combine(AppContext.BaseDirectory, "Golden", "simulator-core.trace");
        var golden = File.ReadAllText(goldenPath).ReplaceLineEndings("\n");

        Assert.Equal(first, second);
        Assert.Equal(golden, first);
    }

    [Fact]
    public void SnapshotAndDeltaRemainScopedAndOrdered()
    {
        var clock = new DeterministicClock(Start, TimeSpan.FromSeconds(1), 100, 10);
        var scenario = new SimulatorScenario(CreateConfig(), clock);
        var runtime = new CoreRuntime(ScopeId, clock, clock);

        foreach (var observation in scenario.NextStep())
        {
            Assert.True(runtime.Admit(observation).IsSuccess);
        }

        var firstDelta = runtime.GetDelta(new ConsumerCursor<CurrentEntry>(0));
        var firstSnapshot = runtime.GetSnapshot();
        foreach (var observation in scenario.NextStep())
        {
            Assert.True(runtime.Admit(observation).IsSuccess);
        }

        var secondDelta = runtime.GetDelta(firstDelta.To);
        var finalSnapshot = runtime.GetSnapshot();

        Assert.Equal(ScopeId, firstSnapshot.ScopeId);
        Assert.Equal(2, firstSnapshot.Entries.Count);
        Assert.Equal(2, firstDelta.Changes.Count);
        Assert.Equal((ulong)2, firstDelta.To.Value);
        Assert.Equal(2, secondDelta.Changes.Count);
        Assert.Equal((ulong)4, secondDelta.To.Value);
        Assert.Equal(2, finalSnapshot.Entries.Count);
        Assert.Equal([3UL, 4UL], finalSnapshot.Entries.Select(entry => entry.CurrentPosition.Value));
    }

    [Fact]
    public void RestartPreservesConfiguredIdentityAndSemanticTrace()
    {
        var beforeRestart = RunTrace();
        var afterRestart = RunTrace();
        var restartedScenario = new SimulatorScenario(
            CreateConfig(),
            new DeterministicClock(Start, TimeSpan.FromSeconds(1), 100, 10));
        var firstObservation = restartedScenario.NextStep()[0];

        Assert.Equal(ScopeId, firstObservation.ScopeId);
        Assert.Equal(SourceId, firstObservation.SourceId);
        Assert.Equal(PointA, firstObservation.PointId);
        Assert.Equal(beforeRestart, afterRestart);
    }

    [Fact]
    public void SimulatorApiCannotReceiveOrReturnMutableCoreRuntime()
    {
        var publicMethods = typeof(SimulatorScenario).GetMethods()
            .Where(method => method.DeclaringType == typeof(SimulatorScenario));
        var publicConstructors = typeof(SimulatorScenario).GetConstructors();

        Assert.DoesNotContain(
            publicMethods,
            method => method.ReturnType == typeof(CoreRuntime) ||
                      method.GetParameters().Any(parameter => parameter.ParameterType == typeof(CoreRuntime)));
        Assert.DoesNotContain(
            publicConstructors,
            constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(CoreRuntime)));
    }

    [Fact]
    public void CoreRejectsObservationFromAnotherScope()
    {
        var clock = new DeterministicClock(Start, TimeSpan.FromSeconds(1), 100, 10);
        var runtime = new CoreRuntime(ScopeId, clock, clock);
        var foreignScope = RuntimeScopeId.From(Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa"));
        var observation = new SourceObservation(
            foreignScope,
            SourceId,
            PointA,
            new OwnerPosition<SourceObservation>(1),
            TypedValue.From(10L),
            Unit.None,
            DataQuality.Good,
            Freshness.Fresh,
            SourceTimestamp.FromUtc(Start));

        var result = runtime.Admit(observation);

        Assert.Equal("core.scope_mismatch", result.Error?.Code.Value);
        Assert.Empty(runtime.GetSnapshot().Entries);
    }

    private static string RunTrace()
    {
        var clock = new DeterministicClock(Start, TimeSpan.FromSeconds(1), 100, 10);
        var scenario = new SimulatorScenario(CreateConfig(), clock);
        var runtime = new CoreRuntime(ScopeId, clock, clock);
        var entries = new List<CurrentEntry>();

        for (var step = 0; step < 2; step++)
        {
            foreach (var observation in scenario.NextStep())
            {
                entries.Add(runtime.Admit(observation).Value);
            }
        }

        return SemanticTraceWriter.Write(entries);
    }

    private static SimulatorScenarioConfig CreateConfig() => new(
        ScopeId,
        SourceId,
        123_456_789,
        [
            new SimulatorPointConfig(PointB, 200, 10, Unit.FromSymbol("bar")),
            new SimulatorPointConfig(PointA, 100, 5, Unit.FromSymbol("°C")),
        ]);

    private sealed class DeterministicClock : IWallClock, IMonotonicClock
    {
        private readonly TimeSpan wallStep;
        private readonly long monotonicStep;
        private DateTimeOffset wall;
        private long monotonic;

        public DeterministicClock(
            DateTimeOffset wall,
            TimeSpan wallStep,
            long monotonic,
            long monotonicStep)
        {
            this.wall = wall;
            this.wallStep = wallStep;
            this.monotonic = monotonic;
            this.monotonicStep = monotonicStep;
        }

        public DateTimeOffset GetUtcNow()
        {
            var current = wall;
            wall = wall.Add(wallStep);
            return current;
        }

        public MonotonicTimestamp GetTimestamp()
        {
            var current = new MonotonicTimestamp(monotonic);
            monotonic = checked(monotonic + monotonicStep);
            return current;
        }

        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) =>
            TimeSpan.FromTicks(checked(finish.Value - start.Value));
    }
}
