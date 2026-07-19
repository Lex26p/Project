using Dispatcher.Configuration;
using Dispatcher.Facilities;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Simulator;

namespace Dispatcher.Server;

public sealed class SimulatorReleaseActivator
{
    private readonly ConfigurationService configuration;
    private readonly SimulatorRuntimeStore simulator;

    public SimulatorReleaseActivator(ConfigurationService configuration, SimulatorRuntimeStore simulator)
    {
        this.configuration = configuration;
        this.simulator = simulator;
    }

    public async Task<Result<ActiveSimulatorManifest>> ActivateDesiredAsync(
        SessionSnapshot? session,
        FacilityScopeId scopeId,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        var desired = await configuration.ReadDesiredReleaseAsync(session, scopeId, cancellationToken)
            .ConfigureAwait(false);
        if (desired.IsFailure)
        {
            return Result.Failure<ActiveSimulatorManifest>(desired.Error!);
        }

        if (desired.Value.DistributedAt is null)
        {
            return Failure("simulator.release_not_distributed", "Desired release was not distributed.");
        }

        var delivery = new SimulatorManifestDelivery(
            desired.Value.RevisionId,
            desired.Value.ScopeId,
            desired.Value.RevisionNumber,
            desired.Value.SourceRevisionId,
            desired.Value.ManifestJson,
            desired.Value.ManifestFingerprint,
            desired.Value.DependencyFingerprint);
        var receipt = await simulator.ReceiveAsync(delivery, workerId, cancellationToken).ConfigureAwait(false);
        if (receipt.IsFailure)
        {
            return Result.Failure<ActiveSimulatorManifest>(receipt.Error!);
        }

        var validation = await simulator.ValidateAsync(scopeId, desired.Value.RevisionId, workerId, cancellationToken)
            .ConfigureAwait(false);
        if (validation.IsFailure)
        {
            return Result.Failure<ActiveSimulatorManifest>(validation.Error!);
        }

        if (validation.Value.Status == SimulatorManifestStatus.Rejected)
        {
            return Failure(
                validation.Value.RejectionCode ?? "simulator.manifest_invalid",
                validation.Value.RejectionMessage ?? "Simulator manifest was rejected.");
        }

        var active = await simulator.ActivateAsync(scopeId, desired.Value.RevisionId, workerId, cancellationToken)
            .ConfigureAwait(false);
        if (active.IsFailure)
        {
            return active;
        }

        var acknowledged = await configuration.AcknowledgeActivationAsync(
            session,
            scopeId,
            desired.Value.RevisionId,
            desired.Value.Version,
            cancellationToken).ConfigureAwait(false);
        return acknowledged.IsFailure
            ? Result.Failure<ActiveSimulatorManifest>(acknowledged.Error!)
            : active;
    }

    private static Result<ActiveSimulatorManifest> Failure(string code, string message) =>
        Result.Failure<ActiveSimulatorManifest>(new OperationError(ErrorCode.From(code), message));
}
