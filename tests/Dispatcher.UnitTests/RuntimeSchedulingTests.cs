using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Facilities;
using Dispatcher.Semantics;
using Dispatcher.Simulator;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class RuntimeSchedulingTests
{
    private static readonly RuntimeScopeId ScopeId = RuntimeScopeId.From(
        Guid.Parse("91000000-0000-0000-0000-000000000001"));
    private static readonly SourceId SourceA = SourceId.From(
        Guid.Parse("92000000-0000-0000-0000-000000000001"));
    private static readonly SourceId SourceB = SourceId.From(
        Guid.Parse("92000000-0000-0000-0000-000000000002"));
    private static readonly PointId PointA = PointId.From(
        Guid.Parse("93000000-0000-0000-0000-000000000001"));
    private static readonly PointId PointB = PointId.From(
        Guid.Parse("93000000-0000-0000-0000-000000000002"));
    private static readonly DateTimeOffset Start = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(RuntimeFactClass.CurrentCheckpoint, RuntimeFactProtection.Rebuildable)]
    [InlineData(RuntimeFactClass.SourceCut, RuntimeFactProtection.Protected)]
    [InlineData(RuntimeFactClass.SourceGap, RuntimeFactProtection.Protected)]
    public void RuntimeFactsHaveExplicitRecoveryClassification(
        RuntimeFactClass factClass,
        RuntimeFactProtection expected)
    {
        Assert.Equal(expected, RuntimeFactClassifier.Classify(factClass));
    }

    [Fact]
    public void SchedulerBoundsBindingsAndInFlightAttemptsWithExplicitMissedPolicy()
    {
        var clock = new MutableClock(Start);
        var scheduler = new BoundedPollScheduler(
            ScopeId,
            new PollScheduleLimits(TimeSpan.FromSeconds(10), 2, 1),
            clock);
        var bindingA = Binding(SourceA, 1, 1);
        var bindingB = Binding(SourceB, 1, 1);
        var bindingC = Binding(
            SourceId.From(Guid.Parse("92000000-0000-0000-0000-000000000003")),
            1,
            1);
        Assert.True(scheduler.ActivateBinding(bindingA).IsSuccess);
        Assert.True(scheduler.ActivateBinding(bindingB).IsSuccess);
        Assert.Equal("poll.binding_capacity", scheduler.ActivateBinding(bindingC).Error?.Code.Value);

        var first = scheduler.TryStart(bindingA, 1).Value;
        Assert.Equal(PollStartStatus.Started, first.Status);
        Assert.Equal(PollStartStatus.MissedOverlap, scheduler.TryStart(bindingA, 2).Value.Status);
        Assert.Equal(PollStartStatus.MissedCapacity, scheduler.TryStart(bindingB, 1).Value.Status);
        Assert.Equal(new PollSchedulerSnapshot(2, 1, 1, 1), scheduler.GetSnapshot());

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(first.Attempt, Assert.Single(scheduler.ExpireTimedOut()));
        Assert.Equal(0, scheduler.GetSnapshot().InFlightCount);
        Assert.Equal(
            PollCompletionStatus.Stale,
            scheduler.Complete(first.Attempt!, [Observation(bindingA, PointA, 1, 10)]).Value.Status);

        var next = scheduler.TryStart(bindingA, 3);
        Assert.Equal(PollStartStatus.Started, next.Value.Status);
        var completion = scheduler.Complete(
            next.Value.Attempt!,
            [Observation(bindingA, PointA, 1, 10)]);
        Assert.Equal(PollCompletionStatus.Completed, completion.Value.Status);
        Assert.NotNull(completion.Value.Cut);
    }

    [Fact]
    public void RebindingFencesLateSchedulerAndCoreResults()
    {
        var clock = new MutableClock(Start);
        var scheduler = new BoundedPollScheduler(
            ScopeId,
            new PollScheduleLimits(TimeSpan.FromMinutes(1), 1, 1),
            clock);
        var runtime = new CoreRuntime(ScopeId, clock, clock);
        var firstBinding = Binding(SourceA, 1, 1);
        var nextBinding = Binding(SourceA, 2, 1);
        Assert.True(scheduler.ActivateBinding(firstBinding).IsSuccess);
        Assert.True(runtime.ActivateBinding(firstBinding).IsSuccess);
        var oldAttempt = scheduler.TryStart(firstBinding, 1).Value.Attempt!;
        var oldCut = RuntimeCut.Normalize(
            firstBinding,
            1,
            [Observation(firstBinding, PointA, 1, 10)]).Value;

        Assert.True(scheduler.ActivateBinding(nextBinding).IsSuccess);
        Assert.True(runtime.ActivateBinding(nextBinding).IsSuccess);
        Assert.Equal(
            PollCompletionStatus.Stale,
            scheduler.Complete(oldAttempt, [Observation(firstBinding, PointA, 1, 10)]).Value.Status);
        Assert.Equal("core.binding_stale", runtime.Apply(oldCut).Error?.Code.Value);
        Assert.Empty(runtime.GetSnapshot().Entries);
        Assert.Empty(runtime.GetLivenessSnapshot().Sources);

        var nextAttempt = scheduler.TryStart(nextBinding, 1).Value.Attempt!;
        var completion = scheduler.Complete(
            nextAttempt,
            [Observation(nextBinding, PointA, 1, 20)]).Value;
        var accepted = runtime.Apply(completion.Cut!);
        Assert.True(accepted.IsSuccess);
        Assert.Equal(SourceBindingGeneration.From(2), accepted.Value.CurrentTransitions[0].BindingGeneration);
    }

    [Fact]
    public void UnchangedObservationAdvancesOnlyLiveness()
    {
        var clock = new MutableClock(Start);
        var runtime = new CoreRuntime(ScopeId, clock, clock);
        var binding = Binding(SourceA, 1, 1);
        Assert.True(runtime.ActivateBinding(binding).IsSuccess);

        var first = Apply(runtime, binding, 1, [Observation(binding, PointA, 1, 10)]);
        Assert.Single(first.Value.CurrentTransitions);
        var currentCursor = runtime.GetSnapshot().Position;
        var firstLiveness = runtime.GetLivenessSnapshot().Position;
        clock.Advance(TimeSpan.FromSeconds(1));

        var unchanged = Apply(runtime, binding, 2, [Observation(binding, PointA, 2, 10)]);
        Assert.Empty(unchanged.Value.CurrentTransitions);
        Assert.Equal(currentCursor, runtime.GetSnapshot().Position);
        Assert.Empty(runtime.GetDelta(new ConsumerCursor<CurrentEntry>(currentCursor.Value)).Changes);
        var liveness = runtime.GetLivenessSnapshot();
        Assert.Equal(checked(firstLiveness.Value + 1), liveness.Position.Value);
        Assert.Equal((ulong)2, Assert.Single(liveness.Sources).LastSourcePosition);

        var freshnessChanged = Apply(
            runtime,
            binding,
            3,
            [Observation(binding, PointA, 3, 10, Freshness.Stale)]);
        Assert.Single(freshnessChanged.Value.CurrentTransitions);
        Assert.Equal(checked(currentCursor.Value + 1), runtime.GetSnapshot().Position.Value);
    }

    [Fact]
    public void RuntimeCutNormalizesOrderAndRejectsPartialPositionAdvance()
    {
        var clock = new MutableClock(Start);
        var runtime = new CoreRuntime(ScopeId, clock, clock);
        var binding = Binding(SourceA, 1, 1);
        Assert.True(runtime.ActivateBinding(binding).IsSuccess);
        var cut = RuntimeCut.Normalize(
            binding,
            1,
            [
                Observation(binding, PointB, 1, 20),
                Observation(binding, PointA, 2, 10),
            ]);
        Assert.Equal([PointA, PointB], cut.Value.Observations.Select(item => item.PointId));

        var accepted = runtime.Apply(cut.Value);
        Assert.Equal([PointA, PointB], accepted.Value.CurrentTransitions.Select(item => item.PointId));
        var beforeInvalid = runtime.GetSnapshot();
        var beforeLiveness = runtime.GetLivenessSnapshot();
        var invalid = RuntimeCut.Normalize(
            binding,
            2,
            [Observation(binding, PointA, 4, 30)]);
        Assert.Equal("core.source_position", runtime.Apply(invalid.Value).Error?.Code.Value);
        var afterInvalid = runtime.GetSnapshot();
        var afterLiveness = runtime.GetLivenessSnapshot();
        Assert.Equal(beforeInvalid.Position, afterInvalid.Position);
        Assert.True(beforeInvalid.Entries.SequenceEqual(afterInvalid.Entries));
        Assert.Equal(beforeLiveness.Position, afterLiveness.Position);
        Assert.True(beforeLiveness.Sources.SequenceEqual(afterLiveness.Sources));

        var duplicate = RuntimeCut.Normalize(
            binding,
            2,
            [
                Observation(binding, PointA, 3, 30),
                Observation(binding, PointA, 4, 40),
            ]);
        Assert.Equal("core.cut_duplicate_point", duplicate.Error?.Code.Value);
    }

    [Fact]
    public void SimulatorSourceUsesActiveManifestAndSessionGenerations()
    {
        var clock = new MutableClock(Start);
        var config = new SimulatorScenarioConfig(
            ScopeId,
            SourceA,
            42,
            [new SimulatorPointConfig(PointA, 10, 0, Unit.FromSymbol("kW"))]);
        var receipt = new SimulatorManifestReceipt(
            ConfigurationRevisionId.New(),
            FacilityScopeId.From(ScopeId.Value),
            RevisionNumber.Initial,
            null,
            new string('A', 64),
            new string('B', 64),
            SimulatorManifestStatus.Activated,
            null,
            null,
            Start,
            Start,
            Start);
        var source = new SimulatorPollingSource(
            new ActiveSimulatorManifest(receipt, config, 7),
            SourceSessionGeneration.From(3),
            clock);
        var attempt = new PollAttemptToken(source.Binding, 1, clock.GetTimestamp());

        var observation = Assert.Single(source.Poll(attempt));

        Assert.Equal(SourceBindingGeneration.From(7), source.Binding.BindingGeneration);
        Assert.Equal(SourceSessionGeneration.From(3), source.Binding.SessionGeneration);
        Assert.Equal(SourceA, observation.SourceId);
    }

    private static SourceBinding Binding(
        SourceId sourceId,
        ulong bindingGeneration,
        ulong sessionGeneration) =>
        new(
            ScopeId,
            sourceId,
            SourceBindingGeneration.From(bindingGeneration),
            SourceSessionGeneration.From(sessionGeneration));

    private static SourceObservation Observation(
        SourceBinding binding,
        PointId pointId,
        ulong sourcePosition,
        long value,
        Freshness freshness = Freshness.Fresh) =>
        new(
            binding.ScopeId,
            binding.SourceId,
            pointId,
            new OwnerPosition<SourceObservation>(sourcePosition),
            TypedValue.From(value),
            Unit.FromSymbol("kW"),
            DataQuality.Good,
            freshness,
            SourceTimestamp.FromUtc(Start.AddSeconds(checked((long)sourcePosition))));

    private static Result<RuntimeCutAcceptance> Apply(
        CoreRuntime runtime,
        SourceBinding binding,
        ulong scheduleSequence,
        IReadOnlyList<SourceObservation> observations)
    {
        var cut = RuntimeCut.Normalize(binding, scheduleSequence, observations);
        return cut.IsSuccess
            ? runtime.Apply(cut.Value)
            : Result.Failure<RuntimeCutAcceptance>(cut.Error!);
    }

    private sealed class MutableClock : IWallClock, IMonotonicClock
    {
        private DateTimeOffset wall;
        private long monotonicTicks;

        public MutableClock(DateTimeOffset wall)
        {
            this.wall = wall;
        }

        public DateTimeOffset GetUtcNow() => wall;

        public MonotonicTimestamp GetTimestamp() => new(monotonicTicks);

        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) =>
            TimeSpan.FromTicks(checked(finish.Value - start.Value));

        public void Advance(TimeSpan duration)
        {
            wall = wall.Add(duration);
            monotonicTicks = checked(monotonicTicks + duration.Ticks);
        }
    }
}
