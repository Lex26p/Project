using Dispatcher.Semantics;

namespace Dispatcher.RuntimeHost;

public enum RuntimeHostApplicationState
{
    Created = 1,
    Starting = 2,
    Reconciling = 3,
    Polling = 4,
    WaitingToRetry = 5,
    Stopping = 6,
    Stopped = 7,
    Faulted = 8,
}

public enum RuntimeHostSessionCycleStatus
{
    NoActiveManifest = 1,
    WorkerStopped = 2,
}

public sealed record RuntimeHostSessionCycleResult(RuntimeHostSessionCycleStatus Status);

public sealed record RuntimeHostApplicationSnapshot(
    RuntimeHostApplicationState State,
    ulong SessionAttempt,
    ulong RetryCount,
    string? ReasonCode,
    TimeSpan? RetryDelay);

public interface IRuntimeHostSession : IAsyncDisposable
{
    Task<Result> StartAsync(CancellationToken cancellationToken);

    Task<Result<RuntimeHostSessionCycleResult>> RunSimulatorCycleAsync(
        CancellationToken cancellationToken);

    Task<Result> StopAsync(CancellationToken cancellationToken);
}

public delegate ValueTask<IRuntimeHostSession> RuntimeHostSessionFactory(
    CancellationToken cancellationToken);

public delegate bool RuntimeHostTransientFailureClassifier(Exception exception);

public delegate Task RuntimeHostRetryDelay(TimeSpan delay, CancellationToken cancellationToken);

public delegate void RuntimeHostApplicationObserver(RuntimeHostApplicationSnapshot snapshot);

public sealed class RuntimeHostApplication
{
    private const string MissingManifestReason = "runtime.simulator_manifest_missing";
    private const string TransientFailureReason = "runtime.transient_failure";
    private readonly RuntimeHostSessionFactory createSession;
    private readonly RuntimeHostTransientFailureClassifier isTransientFailure;
    private readonly RuntimeHostRetryDelay delay;
    private readonly RuntimeHostApplicationObserver? observer;
    private readonly TimeSpan initialBackoff;
    private readonly TimeSpan maximumBackoff;
    private readonly object snapshotSync = new();
    private RuntimeHostApplicationSnapshot snapshot = new(
        RuntimeHostApplicationState.Created,
        0,
        0,
        null,
        null);
    private int runClaimed;

    public RuntimeHostApplication(
        RuntimeHostSessionFactory createSession,
        TimeSpan initialBackoff,
        TimeSpan maximumBackoff,
        RuntimeHostTransientFailureClassifier isTransientFailure,
        RuntimeHostRetryDelay? delay = null,
        RuntimeHostApplicationObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(createSession);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialBackoff, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumBackoff, TimeSpan.Zero);
        if (initialBackoff > maximumBackoff)
        {
            throw new ArgumentException(
                "Initial runtime retry backoff must not exceed maximum backoff.",
                nameof(initialBackoff));
        }

        ArgumentNullException.ThrowIfNull(isTransientFailure);
        this.createSession = createSession;
        this.initialBackoff = initialBackoff;
        this.maximumBackoff = maximumBackoff;
        this.isTransientFailure = isTransientFailure;
        this.delay = delay ?? Task.Delay;
        this.observer = observer;
    }

    public async Task<Result> RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref runClaimed, 1, 0) != 0)
        {
            return Failure(
                "runtime.application_state",
                "Runtime host application can only run once.");
        }

        var sessionAttempt = 0UL;
        var retryCount = 0UL;
        var retryDelay = initialBackoff;
        while (true)
        {
            IRuntimeHostSession? session = null;
            var started = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                sessionAttempt = checked(sessionAttempt + 1);
                Publish(RuntimeHostApplicationState.Starting, sessionAttempt, retryCount, null, null);
                session = await createSession(cancellationToken).ConfigureAwait(false);
                var startedResult = await session.StartAsync(cancellationToken).ConfigureAwait(false);
                if (startedResult.IsFailure)
                {
                    await StopBestEffortAsync(session, started, cancellationToken).ConfigureAwait(false);
                    return Fault(sessionAttempt, retryCount, startedResult.Error!);
                }

                started = true;
                retryDelay = initialBackoff;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Publish(RuntimeHostApplicationState.Reconciling, sessionAttempt, retryCount, null, null);
                    var cycle = await session.RunSimulatorCycleAsync(cancellationToken).ConfigureAwait(false);
                    if (cycle.IsFailure)
                    {
                        await StopBestEffortAsync(session, started, cancellationToken).ConfigureAwait(false);
                        return Fault(sessionAttempt, retryCount, cycle.Error!);
                    }

                    if (cycle.Value.Status == RuntimeHostSessionCycleStatus.WorkerStopped)
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            await StopBestEffortAsync(session, started, cancellationToken).ConfigureAwait(false);
                            return Fault(
                                sessionAttempt,
                                retryCount,
                                new OperationError(
                                    ErrorCode.From("runtime.worker_stopped"),
                                    "Simulator polling worker stopped without application cancellation."));
                        }

                        var stopped = await StopForShutdownAsync(session, sessionAttempt, retryCount)
                            .ConfigureAwait(false);
                        return stopped;
                    }

                    retryCount = checked(retryCount + 1);
                    Publish(
                        RuntimeHostApplicationState.WaitingToRetry,
                        sessionAttempt,
                        retryCount,
                        MissingManifestReason,
                        retryDelay);
                    await delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    retryDelay = NextBackoff(retryDelay);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (session is not null && started)
                {
                    return await StopForShutdownAsync(session, sessionAttempt, retryCount).ConfigureAwait(false);
                }

                Publish(RuntimeHostApplicationState.Stopped, sessionAttempt, retryCount, null, null);
                return Result.Success();
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException and not StackOverflowException &&
                isTransientFailure(exception))
            {
                if (session is not null)
                {
                    await StopBestEffortAsync(session, started, cancellationToken).ConfigureAwait(false);
                }

                retryCount = checked(retryCount + 1);
                Publish(
                    RuntimeHostApplicationState.WaitingToRetry,
                    sessionAttempt,
                    retryCount,
                    TransientFailureReason,
                    retryDelay);
                try
                {
                    await delay(retryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Publish(RuntimeHostApplicationState.Stopped, sessionAttempt, retryCount, null, null);
                    return Result.Success();
                }

                retryDelay = NextBackoff(retryDelay);
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException and not StackOverflowException)
            {
                if (session is not null)
                {
                    await StopBestEffortAsync(session, started, cancellationToken).ConfigureAwait(false);
                }

                return Fault(
                    sessionAttempt,
                    retryCount,
                    new OperationError(
                        ErrorCode.From("runtime.application_failed"),
                        "Runtime host application failed without exposing configuration or secrets."));
            }
            finally
            {
                if (session is not null)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    public RuntimeHostApplicationSnapshot GetSnapshot()
    {
        lock (snapshotSync)
        {
            return snapshot;
        }
    }

    private async Task<Result> StopForShutdownAsync(
        IRuntimeHostSession session,
        ulong sessionAttempt,
        ulong retryCount)
    {
        Publish(RuntimeHostApplicationState.Stopping, sessionAttempt, retryCount, null, null);
        try
        {
            var stopped = await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            if (stopped.IsFailure)
            {
                return Fault(sessionAttempt, retryCount, stopped.Error!);
            }
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            return Fault(
                sessionAttempt,
                retryCount,
                new OperationError(
                    ErrorCode.From("runtime.shutdown_failed"),
                    "Runtime host shutdown failed without exposing configuration or secrets."));
        }

        Publish(RuntimeHostApplicationState.Stopped, sessionAttempt, retryCount, null, null);
        return Result.Success();
    }

    private static async Task StopBestEffortAsync(
        IRuntimeHostSession session,
        bool started,
        CancellationToken cancellationToken)
    {
        if (!started || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _ = await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            _ = exception;
        }
    }

    private TimeSpan NextBackoff(TimeSpan current)
    {
        if (current >= maximumBackoff)
        {
            return maximumBackoff;
        }

        var doubledTicks = current.Ticks > maximumBackoff.Ticks / 2
            ? maximumBackoff.Ticks
            : checked(current.Ticks * 2);
        return TimeSpan.FromTicks(Math.Min(doubledTicks, maximumBackoff.Ticks));
    }

    private Result Fault(ulong sessionAttempt, ulong retryCount, OperationError error)
    {
        Publish(
            RuntimeHostApplicationState.Faulted,
            sessionAttempt,
            retryCount,
            error.Code.Value,
            null);
        return Result.Failure(error);
    }

    private void Publish(
        RuntimeHostApplicationState state,
        ulong sessionAttempt,
        ulong retryCount,
        string? reasonCode,
        TimeSpan? retryDelay)
    {
        RuntimeHostApplicationSnapshot current;
        lock (snapshotSync)
        {
            snapshot = new RuntimeHostApplicationSnapshot(
                state,
                sessionAttempt,
                retryCount,
                reasonCode,
                retryDelay);
            current = snapshot;
        }

        observer?.Invoke(current);
    }

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));
}
