using Dispatcher.RuntimeHost;
using Dispatcher.Semantics;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class RuntimeHostApplicationTests
{
    [Fact]
    public async Task MissingManifestUsesCappedBackoffWithoutRestartingSession()
    {
        var session = new ScriptedSession(
            [
                Result.Success(new RuntimeHostSessionCycleResult(
                    RuntimeHostSessionCycleStatus.NoActiveManifest)),
                Result.Success(new RuntimeHostSessionCycleResult(
                    RuntimeHostSessionCycleStatus.NoActiveManifest)),
                Result.Success(new RuntimeHostSessionCycleResult(
                    RuntimeHostSessionCycleStatus.NoActiveManifest)),
            ]);
        var delays = new List<TimeSpan>();
        using var cancellation = new CancellationTokenSource();
        var application = new RuntimeHostApplication(
            _ => ValueTask.FromResult<IRuntimeHostSession>(session),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(25),
            _ => false,
            (delay, token) =>
            {
                delays.Add(delay);
                if (delays.Count == 3)
                {
                    cancellation.Cancel();
                    return Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return Task.CompletedTask;
            });

        var result = await application.RunAsync(cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(25),
            ],
            delays);
        Assert.Equal(1, session.StartCalls);
        Assert.Equal(3, session.CycleCalls);
        Assert.Equal(1, session.StopCalls);
        Assert.Equal(1, session.DisposeCalls);
        Assert.Equal(RuntimeHostApplicationState.Stopped, application.GetSnapshot().State);
    }

    [Fact]
    public async Task TransientStartupFailureRecreatesSessionAfterBoundedDelay()
    {
        var failed = new ScriptedSession([], new TimeoutException("temporary database outage"));
        var running = new ScriptedSession(
            [
                Result.Success(new RuntimeHostSessionCycleResult(
                    RuntimeHostSessionCycleStatus.NoActiveManifest)),
            ]);
        var sessions = new Queue<ScriptedSession>([failed, running]);
        var delays = new List<TimeSpan>();
        using var cancellation = new CancellationTokenSource();
        var application = new RuntimeHostApplication(
            _ => ValueTask.FromResult<IRuntimeHostSession>(sessions.Dequeue()),
            TimeSpan.FromMilliseconds(15),
            TimeSpan.FromMilliseconds(60),
            exception => exception is TimeoutException,
            (delay, token) =>
            {
                delays.Add(delay);
                if (delays.Count == 2)
                {
                    cancellation.Cancel();
                    return Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return Task.CompletedTask;
            });

        var result = await application.RunAsync(cancellation.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(15)],
            delays);
        Assert.Equal(1, failed.StartCalls);
        Assert.Equal(0, failed.StopCalls);
        Assert.Equal(1, failed.DisposeCalls);
        Assert.Equal(1, running.StartCalls);
        Assert.Equal(1, running.CycleCalls);
        Assert.Equal(1, running.StopCalls);
        Assert.Equal(1, running.DisposeCalls);
        Assert.Equal((ulong)2, application.GetSnapshot().SessionAttempt);
    }

    [Fact]
    public async Task FatalCycleFailureStopsSessionWithoutRetry()
    {
        var error = new OperationError(
            ErrorCode.From("runtime.test_invariant"),
            "A test invariant failed.");
        var session = new ScriptedSession(
            [Result.Failure<RuntimeHostSessionCycleResult>(error)]);
        var delayCalled = false;
        var application = new RuntimeHostApplication(
            _ => ValueTask.FromResult<IRuntimeHostSession>(session),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(20),
            _ => false,
            (_, _) =>
            {
                delayCalled = true;
                return Task.CompletedTask;
            });

        var result = await application.RunAsync();

        Assert.Equal("runtime.test_invariant", result.Error?.Code.Value);
        Assert.False(delayCalled);
        Assert.Equal(1, session.StartCalls);
        Assert.Equal(1, session.CycleCalls);
        Assert.Equal(1, session.StopCalls);
        Assert.Equal(1, session.DisposeCalls);
        Assert.Equal(RuntimeHostApplicationState.Faulted, application.GetSnapshot().State);
    }

    private sealed class ScriptedSession : IRuntimeHostSession
    {
        private readonly Queue<Result<RuntimeHostSessionCycleResult>> cycles;
        private readonly Exception? startException;

        public ScriptedSession(
            Result<RuntimeHostSessionCycleResult>[] cycles,
            Exception? startException = null)
        {
            this.cycles = new Queue<Result<RuntimeHostSessionCycleResult>>(cycles);
            this.startException = startException;
        }

        public int StartCalls { get; private set; }

        public int CycleCalls { get; private set; }

        public int StopCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task<Result> StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            if (startException is not null)
            {
                throw startException;
            }

            return Task.FromResult(Result.Success());
        }

        public Task<Result<RuntimeHostSessionCycleResult>> RunSimulatorCycleAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CycleCalls++;
            return Task.FromResult(cycles.Dequeue());
        }

        public Task<Result> StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            return Task.FromResult(Result.Success());
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
