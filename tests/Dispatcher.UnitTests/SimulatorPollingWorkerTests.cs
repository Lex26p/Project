using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Facilities;
using Dispatcher.RuntimeHost;
using Dispatcher.Semantics;
using Dispatcher.Simulator;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class SimulatorPollingWorkerTests
{
    private static readonly RuntimeScopeId ScopeId = RuntimeScopeId.From(
        Guid.Parse("b4000000-0000-0000-0000-000000000001"));
    private static readonly SourceId SourceId = Dispatcher.Core.SourceId.From(
        Guid.Parse("b5000000-0000-0000-0000-000000000001"));
    private static readonly DateTimeOffset Start = new(2026, 7, 26, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompletedPollIsEnqueuedAndProcessedSequentiallyBeforeDelay()
    {
        var clock = new ScriptedClock(Start, [0, 1]);
        var source = CreateSource(clock);
        var scheduler = new BoundedPollScheduler(
            ScopeId,
            new PollScheduleLimits(TimeSpan.FromSeconds(1), maxBindings: 1, maxInFlight: 1),
            clock);
        var operations = new List<string>();
        var processCalls = 0;
        using var cancellation = new CancellationTokenSource();
        var worker = new SimulatorPollingWorker(
            source,
            scheduler,
            TimeSpan.FromSeconds(1),
            maxProcessBatch: 4,
            binding =>
            {
                Assert.Equal(source.Binding, binding);
                operations.Add("activate");
                return Result.Success();
            },
            (cut, _) =>
            {
                Assert.Equal(source.Binding, cut.Binding);
                Assert.Single(cut.Observations);
                operations.Add("enqueue");
                return Task.FromResult(Result.Success(
                    new RuntimeIngressResult(RuntimeIngressStatus.Queued, null)));
            },
            _ =>
            {
                processCalls++;
                operations.Add("process");
                return Task.FromResult(Result.Success(processCalls == 1));
            },
            (_, token) =>
            {
                cancellation.Cancel();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        var result = await worker.RunAsync(cancellation.Token);
        var snapshot = worker.GetSnapshot();

        Assert.True(result.IsSuccess);
        Assert.Equal(["activate", "enqueue", "process", "process"], operations);
        Assert.Equal(SimulatorPollingWorkerState.Stopped, snapshot.State);
        Assert.Equal((ulong)1, snapshot.ScheduleSequence);
        Assert.Equal((ulong)1, snapshot.CompletedPolls);
        Assert.Equal((ulong)1, snapshot.AdmittedCuts);
        Assert.Equal((ulong)1, snapshot.ProcessedObligations);
        Assert.Null(snapshot.LastErrorCode);
    }

    [Fact]
    public async Task TimedOutPollDoesNotCreateRuntimeCut()
    {
        var clock = new ScriptedClock(Start, [0, TimeSpan.FromMilliseconds(20).Ticks]);
        var source = CreateSource(clock);
        var scheduler = new BoundedPollScheduler(
            ScopeId,
            new PollScheduleLimits(TimeSpan.FromMilliseconds(10), maxBindings: 1, maxInFlight: 1),
            clock);
        var enqueueCalled = false;
        var processCalled = false;
        using var cancellation = new CancellationTokenSource();
        var worker = new SimulatorPollingWorker(
            source,
            scheduler,
            TimeSpan.FromSeconds(1),
            maxProcessBatch: 4,
            _ => Result.Success(),
            (_, _) =>
            {
                enqueueCalled = true;
                return Task.FromResult(Result.Success(
                    new RuntimeIngressResult(RuntimeIngressStatus.Queued, null)));
            },
            _ =>
            {
                processCalled = true;
                return Task.FromResult(Result.Success(false));
            },
            (_, token) =>
            {
                cancellation.Cancel();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        var result = await worker.RunAsync(cancellation.Token);
        var snapshot = worker.GetSnapshot();

        Assert.True(result.IsSuccess);
        Assert.False(enqueueCalled);
        Assert.False(processCalled);
        Assert.Equal((ulong)1, snapshot.TimedOutPolls);
        Assert.Equal((ulong)0, snapshot.CompletedPolls);
        Assert.Equal((ulong)0, snapshot.AdmittedCuts);
    }

    [Fact]
    public async Task AdmissionFailureFaultsWorkerWithoutRetryingInsidePollLoop()
    {
        var clock = new ScriptedClock(Start, [0, 1]);
        var source = CreateSource(clock);
        var scheduler = new BoundedPollScheduler(
            ScopeId,
            new PollScheduleLimits(TimeSpan.FromSeconds(1), maxBindings: 1, maxInFlight: 1),
            clock);
        var delayCalled = false;
        var worker = new SimulatorPollingWorker(
            source,
            scheduler,
            TimeSpan.FromSeconds(1),
            maxProcessBatch: 4,
            _ => Result.Success(),
            (_, _) => Task.FromResult(Result.Failure<RuntimeIngressResult>(
                new OperationError(ErrorCode.From("runtime.not_accepting"), "Admission is closed."))),
            _ => Task.FromResult(Result.Success(false)),
            (_, _) =>
            {
                delayCalled = true;
                return Task.CompletedTask;
            });

        var result = await worker.RunAsync();
        var snapshot = worker.GetSnapshot();

        Assert.Equal("runtime.not_accepting", result.Error?.Code.Value);
        Assert.Equal(SimulatorPollingWorkerState.Faulted, snapshot.State);
        Assert.Equal("runtime.not_accepting", snapshot.LastErrorCode);
        Assert.False(delayCalled);
        Assert.Equal((ulong)1, snapshot.CompletedPolls);
        Assert.Equal((ulong)0, snapshot.AdmittedCuts);
    }

    private static SimulatorPollingSource CreateSource(IWallClock clock)
    {
        var receipt = new SimulatorManifestReceipt(
            ConfigurationRevisionId.New(),
            FacilityScopeId.From(ScopeId.Value),
            RevisionNumber.From(1),
            null,
            new string('A', 64),
            new string('B', 64),
            SimulatorManifestStatus.Activated,
            null,
            null,
            Start,
            Start,
            Start);
        var configuration = new SimulatorScenarioConfig(
            ScopeId,
            SourceId,
            17,
            [
                new SimulatorPointConfig(
                    PointId.From(Guid.Parse("b7000000-0000-0000-0000-000000000001")),
                    25,
                    0,
                    Unit.FromSymbol("°C")),
            ]);
        return new SimulatorPollingSource(
            new ActiveSimulatorManifest(receipt, configuration, 1),
            SourceSessionGeneration.From(1),
            clock);
    }

    private sealed class ScriptedClock : IWallClock, IMonotonicClock
    {
        private readonly DateTimeOffset now;
        private readonly Queue<long> timestamps;
        private long lastTimestamp;

        public ScriptedClock(DateTimeOffset now, IEnumerable<long> timestamps)
        {
            this.now = now;
            this.timestamps = new Queue<long>(timestamps);
        }

        public DateTimeOffset GetUtcNow() => now;

        public MonotonicTimestamp GetTimestamp()
        {
            if (timestamps.Count > 0)
            {
                lastTimestamp = timestamps.Dequeue();
            }

            return new MonotonicTimestamp(lastTimestamp);
        }

        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) =>
            TimeSpan.FromTicks(checked(finish.Value - start.Value));
    }
}
