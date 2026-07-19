using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dispatcher.Core;
using Dispatcher.Semantics;
using Dispatcher.Simulator;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class SimulatorRuntimeCorpusTests
{
    private static readonly RuntimeScopeId ScopeId = RuntimeScopeId.From(
        Guid.Parse("a1000000-0000-7000-8000-000000000001"));
    private static readonly SourceId SourceId = SourceId.From(
        Guid.Parse("a2000000-0000-7000-8000-000000000001"));
    private static readonly DateTimeOffset Start = new(2026, 7, 19, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BurstScenarioMatchesGoldenAndKeepsCurrentStateBounded()
    {
        var first = RunBurstTrace();
        var second = RunBurstTrace();
        var goldenPath = Path.Combine(AppContext.BaseDirectory, "Golden", "simulator-burst.trace");
        var golden = File.ReadAllText(goldenPath).ReplaceLineEndings("\n");
        Assert.Equal(first.Trace, second.Trace);
        Assert.Equal(golden, first.Trace);
        Assert.Equal(new RuntimeCurrentCapacity(4, 4, 6, 6), first.Capacity);
    }

    [Fact]
    public void SeededScenariosPreserveOrderingAndDeterminismAcrossPropertyCorpus()
    {
        for (ulong seed = 0; seed < 32; seed++)
        {
            var pointCount = checked((int)(seed % 8) + 1);
            var first = RunPropertyTrace(seed, pointCount);
            var second = RunPropertyTrace(seed, pointCount);

            Assert.Equal(first.Trace, second.Trace);
            Assert.Equal(pointCount, first.Capacity.PointCount);
            Assert.InRange(first.Capacity.RetainedChangeCount, 1, first.Capacity.RetainedChangeCapacity);
        }
    }

    [Fact]
    public void MassTimeoutExpirationIsDeterministicAndClearsBoundedInFlightState()
    {
        const int sourceCount = 8;
        var clock = new FaultClock(Start);
        var scheduler = new BoundedPollScheduler(
            ScopeId,
            new PollScheduleLimits(TimeSpan.FromSeconds(5), sourceCount, sourceCount),
            clock);
        var bindings = Enumerable.Range(1, sourceCount)
            .Reverse()
            .Select(index => new SourceBinding(
                ScopeId,
                SourceId.From(Guid.Parse($"a2000000-0000-7000-8000-{index:D12}")),
                SourceBindingGeneration.From(1),
                SourceSessionGeneration.From(1)))
            .ToArray();
        foreach (var binding in bindings)
        {
            Assert.True(scheduler.ActivateBinding(binding).IsSuccess);
            Assert.Equal(PollStartStatus.Started, scheduler.TryStart(binding, 1).Value.Status);
        }

        clock.AdvanceMonotonic(TimeSpan.FromSeconds(5));
        var expired = scheduler.ExpireTimedOut();

        Assert.Equal(
            bindings.OrderBy(item => item.SourceId.Value),
            expired.Select(item => item.Binding));
        Assert.Equal(new PollSchedulerSnapshot(sourceCount, 0, 0, 0), scheduler.GetSnapshot());
    }

    [Fact]
    public void ClockRegressionDoesNotControlMonotonicTimeoutOrSourceOrdering()
    {
        var clock = new FaultClock(Start);
        var config = CreateConfig(7, 1);
        var scenario = new SimulatorScenario(config, clock);
        var binding = Binding(sessionGeneration: 1);
        var scheduler = new BoundedPollScheduler(
            ScopeId,
            new PollScheduleLimits(TimeSpan.FromSeconds(5), maxBindings: 1, maxInFlight: 1),
            clock);
        Assert.True(scheduler.ActivateBinding(binding).IsSuccess);
        var attempt = scheduler.TryStart(binding, 1).Value.Attempt!;
        var first = Assert.Single(scenario.NextStep());

        clock.RegressWall(TimeSpan.FromHours(1));
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(5));
        var second = Assert.Single(scenario.NextStep());

        Assert.True(second.SourceTimestamp.Value < first.SourceTimestamp.Value);
        Assert.Equal(checked(first.SourcePosition.Value + 1), second.SourcePosition.Value);
        Assert.Equal(attempt, Assert.Single(scheduler.ExpireTimedOut()));
        Assert.Equal(0, scheduler.GetSnapshot().InFlightCount);
    }

    [Fact]
    public void RestartRequiresResnapshotAndNewSessionCanResumeFromSourcePositionOne()
    {
        var firstClock = new FaultClock(Start);
        var config = CreateConfig(17, 2);
        var firstScenario = new SimulatorScenario(config, firstClock);
        var firstRuntime = CreateRuntime(firstClock, maxPoints: 2, retainedChanges: 4);
        Assert.True(firstRuntime.ActivateBinding(Binding(sessionGeneration: 1)).IsSuccess);
        Apply(firstRuntime, Binding(sessionGeneration: 1), 1, firstScenario.NextStep());
        Apply(firstRuntime, Binding(sessionGeneration: 1), 2, firstScenario.NextStep());
        var checkpoint = firstRuntime.CaptureCheckpoint();

        var restartClock = new FaultClock(Start);
        var restarted = CreateRuntime(restartClock, maxPoints: 2, retainedChanges: 4);
        Assert.True(restarted.Restore(checkpoint).IsSuccess);
        Assert.Equal(firstRuntime.GetSnapshot().Position, restarted.GetSnapshot().Position);
        Assert.True(firstRuntime.GetSnapshot().Entries.SequenceEqual(restarted.GetSnapshot().Entries));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => restarted.GetDelta(new ConsumerCursor<CurrentEntry>(0)));

        var restartBinding = Binding(sessionGeneration: 2);
        Assert.True(restarted.ActivateBinding(restartBinding).IsSuccess);
        var restartScenario = new SimulatorScenario(config, restartClock);
        var accepted = Apply(restarted, restartBinding, 1, restartScenario.NextStep());

        Assert.Equal([5UL, 6UL], accepted.CurrentTransitions.Select(item => item.CurrentPosition.Value));
        Assert.All(
            accepted.CurrentTransitions,
            transition => Assert.Equal(SourceSessionGeneration.From(2), transition.SessionGeneration));
    }

    [Fact]
    public void SlowConsumerGetsGapWhileRecentConsumerReadsBoundedDelta()
    {
        var clock = new FaultClock(Start);
        var scenario = new SimulatorScenario(CreateConfig(23, 1), clock);
        var runtime = CreateRuntime(clock, maxPoints: 1, retainedChanges: 4);
        var binding = Binding(sessionGeneration: 1);
        Assert.True(runtime.ActivateBinding(binding).IsSuccess);
        for (ulong sequence = 1; sequence <= 10; sequence++)
        {
            Apply(runtime, binding, sequence, scenario.NextStep());
        }

        Assert.Throws<ArgumentOutOfRangeException>(
            () => runtime.GetDelta(new ConsumerCursor<CurrentEntry>(0)));
        var finalPosition = runtime.GetSnapshot().Position.Value;
        Assert.True(finalPosition > 4);
        var retainedFrom = checked(finalPosition - 4);
        var recent = runtime.GetDelta(new ConsumerCursor<CurrentEntry>(retainedFrom));

        Assert.Equal(finalPosition, recent.To.Value);
        Assert.Equal(
            Enumerable.Range(1, 4).Select(offset => checked(retainedFrom + (ulong)offset)),
            recent.Changes.Select(item => item.CurrentPosition.Value));
        Assert.Equal(new RuntimeCurrentCapacity(1, 1, 4, 4), runtime.GetCurrentCapacity());
    }

    [Fact]
    public void PointCapacityFailureIsAtomic()
    {
        var clock = new FaultClock(Start);
        var runtime = CreateRuntime(clock, maxPoints: 1, retainedChanges: 2);
        var binding = Binding(sessionGeneration: 1);
        Assert.True(runtime.ActivateBinding(binding).IsSuccess);
        var observations = new SimulatorScenario(CreateConfig(31, 2), clock).NextStep();

        var result = runtime.Apply(RuntimeCut.Normalize(binding, 1, observations).Value);

        Assert.Equal("core.current_capacity", result.Error?.Code.Value);
        Assert.Empty(runtime.GetSnapshot().Entries);
        Assert.Empty(runtime.GetLivenessSnapshot().Sources);
        Assert.Equal(new RuntimeCurrentCapacity(0, 1, 0, 2), runtime.GetCurrentCapacity());
    }

    [Fact]
    [Trait("Category", "Workload")]
    public void RepresentativeWindowsSoakReportsRawPhaseEvidenceWithoutNormativeThresholds()
    {
        const int pointCount = 16;
        const int steps = 20_000;
        const int retainedChanges = 256;
        var clock = new FaultClock(Start);
        var scenario = new SimulatorScenario(CreateConfig(47, pointCount), clock);
        var runtime = CreateRuntime(clock, pointCount, retainedChanges);
        var binding = Binding(sessionGeneration: 1);
        Assert.True(runtime.ActivateBinding(binding).IsSuccess);
        var generateTicks = 0L;
        var normalizeTicks = 0L;
        var applyTicks = 0L;
        var resnapshotTicks = 0L;
        var resnapshotCount = 0;
        var consumerCursor = new ConsumerCursor<CurrentEntry>(0);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var total = Stopwatch.StartNew();

        for (var step = 1; step <= steps; step++)
        {
            var started = Stopwatch.GetTimestamp();
            var observations = scenario.NextStep();
            generateTicks = checked(generateTicks + Stopwatch.GetTimestamp() - started);
            started = Stopwatch.GetTimestamp();
            var cut = RuntimeCut.Normalize(binding, checked((ulong)step), observations).Value;
            normalizeTicks = checked(normalizeTicks + Stopwatch.GetTimestamp() - started);
            started = Stopwatch.GetTimestamp();
            Assert.True(runtime.Apply(cut).IsSuccess);
            applyTicks = checked(applyTicks + Stopwatch.GetTimestamp() - started);

            if (step % 1_000 == 0)
            {
                started = Stopwatch.GetTimestamp();
                try
                {
                    consumerCursor = runtime.GetDelta(consumerCursor).To;
                }
                catch (ArgumentOutOfRangeException)
                {
                    consumerCursor = new ConsumerCursor<CurrentEntry>(runtime.GetSnapshot().Position.Value);
                    resnapshotCount++;
                }

                resnapshotTicks = checked(resnapshotTicks + Stopwatch.GetTimestamp() - started);
            }
        }

        total.Stop();
        var allocatedBytes = checked(GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
        var capacity = runtime.GetCurrentCapacity();
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"S14_WORKLOAD platform=Windows-x64 points={pointCount} steps={steps} observations={pointCount * steps} " +
                $"elapsed_ms={total.Elapsed.TotalMilliseconds:F3} allocated_bytes={allocatedBytes} " +
                $"generate_ms={ToMilliseconds(generateTicks):F3} normalize_ms={ToMilliseconds(normalizeTicks):F3} " +
                $"apply_ms={ToMilliseconds(applyTicks):F3} resnapshot_ms={ToMilliseconds(resnapshotTicks):F3} " +
                $"resnapshots={resnapshotCount} retained={capacity.RetainedChangeCount}/{capacity.RetainedChangeCapacity}"));

        Assert.Equal(pointCount, runtime.GetSnapshot().Entries.Count);
        Assert.Equal(retainedChanges, capacity.RetainedChangeCount);
        Assert.Equal(retainedChanges, capacity.RetainedChangeCapacity);
        Assert.True(resnapshotCount > 0);
    }

    private static (string Trace, RuntimeCurrentCapacity Capacity) RunBurstTrace()
    {
        var clock = new FaultClock(Start);
        var scenario = new SimulatorScenario(CreateConfig(123_456_789, 4), clock);
        var runtime = CreateRuntime(clock, maxPoints: 4, retainedChanges: 6);
        var binding = Binding(sessionGeneration: 1);
        var trace = new StringBuilder();
        Assert.True(runtime.ActivateBinding(binding).IsSuccess);
        for (ulong sequence = 1; sequence <= 3; sequence++)
        {
            var accepted = Apply(runtime, binding, sequence, scenario.NextStep());
            trace.Append(SemanticTraceWriter.Write(accepted.CurrentTransitions));
        }

        return (trace.ToString(), runtime.GetCurrentCapacity());
    }

    private static (string Trace, RuntimeCurrentCapacity Capacity) RunPropertyTrace(ulong seed, int pointCount)
    {
        var clock = new FaultClock(Start);
        var scenario = new SimulatorScenario(CreateConfig(seed, pointCount), clock);
        var runtime = CreateRuntime(clock, pointCount, retainedChanges: 13);
        var binding = Binding(sessionGeneration: 1);
        var trace = new StringBuilder();
        Assert.True(runtime.ActivateBinding(binding).IsSuccess);
        for (ulong sequence = 1; sequence <= 17; sequence++)
        {
            var accepted = Apply(runtime, binding, sequence, scenario.NextStep());
            Assert.Equal(
                accepted.CurrentTransitions.OrderBy(item => item.PointId.Value),
                accepted.CurrentTransitions);
            trace.Append(SemanticTraceWriter.Write(accepted.CurrentTransitions));
        }

        Assert.Equal(
            checked((ulong)(pointCount * 17)),
            Assert.Single(runtime.GetLivenessSnapshot().Sources).LastSourcePosition);

        return (trace.ToString(), runtime.GetCurrentCapacity());
    }

    private static RuntimeCutAcceptance Apply(
        CoreRuntime runtime,
        SourceBinding binding,
        ulong scheduleSequence,
        IReadOnlyList<SourceObservation> observations)
    {
        var cut = RuntimeCut.Normalize(binding, scheduleSequence, observations);
        Assert.True(cut.IsSuccess);
        var accepted = runtime.Apply(cut.Value);
        Assert.True(accepted.IsSuccess);
        return accepted.Value;
    }

    private static SimulatorScenarioConfig CreateConfig(ulong seed, int pointCount) =>
        new(
            ScopeId,
            SourceId,
            seed,
            Enumerable.Range(1, pointCount)
                .Reverse()
                .Select(index => new SimulatorPointConfig(
                    PointId.From(Guid.Parse($"a3000000-0000-7000-8000-{index:D12}")),
                    baseline: checked(index * 100L),
                    amplitude: index,
                    Unit.FromSymbol("kW"))));

    private static SourceBinding Binding(ulong sessionGeneration) =>
        new(
            ScopeId,
            SourceId,
            SourceBindingGeneration.From(1),
            SourceSessionGeneration.From(sessionGeneration));

    private static CoreRuntime CreateRuntime(
        FaultClock clock,
        int maxPoints,
        int retainedChanges) =>
        new(ScopeId, clock, clock, new RuntimeCurrentLimits(maxPoints, retainedChanges));

    private static double ToMilliseconds(long timestampTicks) =>
        timestampTicks * 1_000d / Stopwatch.Frequency;

    private sealed class FaultClock : IWallClock, IMonotonicClock
    {
        private DateTimeOffset wall;
        private long monotonicTicks;

        public FaultClock(DateTimeOffset wall)
        {
            this.wall = wall;
        }

        public DateTimeOffset GetUtcNow()
        {
            var current = wall;
            wall = wall.AddMilliseconds(1);
            return current;
        }

        public MonotonicTimestamp GetTimestamp() => new(monotonicTicks);

        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) =>
            TimeSpan.FromTicks(checked(finish.Value - start.Value));

        public void RegressWall(TimeSpan duration) => wall = wall.Subtract(duration);

        public void AdvanceMonotonic(TimeSpan duration) =>
            monotonicTicks = checked(monotonicTicks + duration.Ticks);
    }
}
