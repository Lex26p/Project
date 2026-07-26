using Dispatcher.Core;
using Dispatcher.Facilities;
using Dispatcher.Semantics;
using Dispatcher.Simulator;

namespace Dispatcher.RuntimeHost;

public enum SimulatorSourceReconciliationStatus
{
    Ready = 1,
    NoActiveManifest = 2,
}

public sealed record SimulatorSourceReconciliation(
    SimulatorSourceReconciliationStatus Status,
    SimulatorPollingSource? Source);

public delegate Task<Result<ActiveSimulatorManifest>> ActiveSimulatorManifestReader(
    FacilityScopeId scopeId,
    CancellationToken cancellationToken);

public delegate Task<SourceSessionGeneration> SourceSessionGenerationAllocator(
    RuntimeScopeId scopeId,
    SourceId sourceId,
    CancellationToken cancellationToken);

public sealed class SimulatorSourceBootstrap
{
    private readonly RuntimeScopeId scopeId;
    private readonly ActiveSimulatorManifestReader readActiveManifest;
    private readonly SourceSessionGenerationAllocator allocateSessionGeneration;
    private readonly IWallClock wallClock;

    public SimulatorSourceBootstrap(
        RuntimeScopeId scopeId,
        ActiveSimulatorManifestReader readActiveManifest,
        SourceSessionGenerationAllocator allocateSessionGeneration,
        IWallClock wallClock)
    {
        _ = scopeId.Value;
        ArgumentNullException.ThrowIfNull(readActiveManifest);
        ArgumentNullException.ThrowIfNull(allocateSessionGeneration);
        ArgumentNullException.ThrowIfNull(wallClock);
        this.scopeId = scopeId;
        this.readActiveManifest = readActiveManifest;
        this.allocateSessionGeneration = allocateSessionGeneration;
        this.wallClock = wallClock;
    }

    public async Task<Result<SimulatorSourceReconciliation>> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        var active = await readActiveManifest(
            FacilityScopeId.From(scopeId.Value),
            cancellationToken).ConfigureAwait(false);
        if (active.IsFailure)
        {
            return active.Error?.Code.Value == "simulator.active_not_found"
                ? Result.Success(new SimulatorSourceReconciliation(
                    SimulatorSourceReconciliationStatus.NoActiveManifest,
                    null))
                : Result.Failure<SimulatorSourceReconciliation>(active.Error!);
        }

        if (active.Value.Configuration.ScopeId != scopeId)
        {
            return Failure(
                "runtime.simulator_scope_mismatch",
                "The active Simulator manifest belongs to another runtime scope.");
        }

        var sessionGeneration = await allocateSessionGeneration(
            scopeId,
            active.Value.Configuration.SourceId,
            cancellationToken).ConfigureAwait(false);
        var source = new SimulatorPollingSource(active.Value, sessionGeneration, wallClock);
        return Result.Success(new SimulatorSourceReconciliation(
            SimulatorSourceReconciliationStatus.Ready,
            source));
    }

    private static Result<SimulatorSourceReconciliation> Failure(string code, string message) =>
        Result.Failure<SimulatorSourceReconciliation>(
            new OperationError(ErrorCode.From(code), message));
}
