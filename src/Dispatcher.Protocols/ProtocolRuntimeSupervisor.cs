using Dispatcher.Core;
using Dispatcher.Semantics;

namespace Dispatcher.Protocols;

public enum ProtocolSupervisorState
{
    Created = 1,
    Running = 2,
    Draining = 3,
    Stopped = 4,
}

public sealed record ProtocolSupervisorSnapshot(
    ProtocolSupervisorState State,
    int RegisteredSources,
    int InFlightOperations,
    bool AdmissionOpen);

public sealed class ProtocolRuntimeSupervisor
{
    private readonly object sync = new();
    private readonly ProtocolWorkloadIdentity workloadIdentity;
    private readonly int maxSources;
    private readonly Dictionary<SourceId, ProtocolSourceController> sources = [];
    private ProtocolSupervisorState state = ProtocolSupervisorState.Created;
    private int inFlight;
    private TaskCompletionSource? drained;

    public ProtocolRuntimeSupervisor(ProtocolWorkloadIdentity workloadIdentity, int maxSources)
    {
        _ = workloadIdentity.Value ?? throw new ArgumentException("Workload identity must be defined.", nameof(workloadIdentity));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSources);
        this.workloadIdentity = workloadIdentity;
        this.maxSources = maxSources;
    }

    public Result Register(SourceId sourceId, ProtocolSourceController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        lock (sync)
        {
            if (state != ProtocolSupervisorState.Created)
            {
                return Failure("protocol.registration_closed", "Protocol sources can only register before startup.");
            }

            if (controller.WorkloadIdentity != workloadIdentity)
            {
                return Failure("protocol.workload_identity", "Protocol source workload identity does not match its runtime host.");
            }

            if (sources.ContainsKey(sourceId))
            {
                return Failure("protocol.source_duplicate", "A protocol source is already registered.");
            }

            if (sources.Count >= maxSources)
            {
                return Failure("protocol.source_capacity", "The bounded protocol source capacity is exhausted.");
            }

            sources.Add(sourceId, controller);
            return Result.Success();
        }
    }

    public Result Start()
    {
        lock (sync)
        {
            if (state != ProtocolSupervisorState.Created)
            {
                return Failure("protocol.lifecycle_state", "Protocol runtime cannot start from its current state.");
            }

            state = ProtocolSupervisorState.Running;
            return Result.Success();
        }
    }

    public Task<Result<RuntimeCut>> AcquireAsync(
        ProtocolSourceRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request.Binding.SourceId, controller => controller.AcquireAsync(request, cancellationToken));

    public Task<Result<ProtocolDiagnosticResult>> DiagnoseAsync(
        ProtocolDiagnosticRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request.Binding.SourceId, controller => controller.DiagnoseAsync(request, cancellationToken));

    public async Task<Result> StopAsync(CancellationToken cancellationToken = default)
    {
        Task wait;
        lock (sync)
        {
            if (state != ProtocolSupervisorState.Running)
            {
                return Failure("protocol.lifecycle_state", "Only a running protocol runtime can stop.");
            }

            state = ProtocolSupervisorState.Draining;
            if (inFlight == 0)
            {
                state = ProtocolSupervisorState.Stopped;
                return Result.Success();
            }

            drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = drained.Task;
        }

        await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public ProtocolSupervisorSnapshot GetSnapshot()
    {
        lock (sync)
        {
            return new ProtocolSupervisorSnapshot(
                state,
                sources.Count,
                inFlight,
                state == ProtocolSupervisorState.Running);
        }
    }

    private async Task<Result<TValue>> ExecuteAsync<TValue>(
        SourceId sourceId,
        Func<ProtocolSourceController, Task<Result<TValue>>> operation)
    {
        ProtocolSourceController controller;
        lock (sync)
        {
            if (state != ProtocolSupervisorState.Running)
            {
                return Failure<TValue>("protocol.not_accepting", "Protocol runtime is not accepting operations.");
            }

            if (!sources.TryGetValue(sourceId, out controller!))
            {
                return Failure<TValue>("protocol.source_unknown", "Protocol source is not registered.");
            }

            inFlight = checked(inFlight + 1);
        }

        try
        {
            return await operation(controller).ConfigureAwait(false);
        }
        finally
        {
            lock (sync)
            {
                inFlight--;
                if (state == ProtocolSupervisorState.Draining && inFlight == 0)
                {
                    state = ProtocolSupervisorState.Stopped;
                    drained?.TrySetResult();
                }
            }
        }
    }

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));
}
