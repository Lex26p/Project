using Dispatcher.Alarm;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Facilities;
using Dispatcher.Semantics;
using Dispatcher.Simulator;

namespace Dispatcher.RuntimeHost;

public enum RuntimeConfigurationReconciliationStatus
{
    NoDeployment = 1,
    Rejected = 2,
    Prepared = 3,
}

public sealed record PreparedRuntimeConfiguration(
    RuntimeConfigurationActivationPlan Plan,
    ConfigurationWorkloadClaim Claim);

public sealed record RuntimeConfigurationReconciliation(
    RuntimeConfigurationReconciliationStatus Status,
    PreparedRuntimeConfiguration? Prepared);

public sealed record ActivatedRuntimeConfiguration(
    ConfigurationRevisionId RevisionId,
    long RuntimeGeneration,
    RevisionNumber AlarmDefinitionEpoch,
    SimulatorPollingSource Source);

public enum RuntimeConfigurationActivationPoint
{
    Claimed = 1,
    Prepared = 2,
    Switched = 3,
}

public sealed class RuntimeConfigurationActivationHook
{
    private readonly Func<RuntimeConfigurationActivationPoint, CancellationToken, Task> callback;

    public RuntimeConfigurationActivationHook(
        Func<RuntimeConfigurationActivationPoint, CancellationToken, Task> callback) =>
        this.callback = callback ?? throw new ArgumentNullException(nameof(callback));

    public Task InvokeAsync(
        RuntimeConfigurationActivationPoint point,
        CancellationToken cancellationToken) =>
        callback(point, cancellationToken);
}

public sealed class RuntimeConfigurationReconciler
{
    private readonly FacilityScopeId scopeId;
    private readonly string workloadIdentity;
    private readonly TimeSpan leaseDuration;
    private readonly ConfigurationWorkloadDeploymentStore deployments;
    private readonly SimulatorRuntimeStore simulator;
    private readonly AlarmStore alarms;
    private readonly SourceSessionGenerationAllocator allocateSessionGeneration;
    private readonly RuntimeDefinitionBindingState definitionBinding;
    private readonly IWallClock wallClock;
    private readonly RuntimeConfigurationActivationHook? hook;

    public RuntimeConfigurationReconciler(
        FacilityScopeId scopeId,
        string workloadIdentity,
        TimeSpan leaseDuration,
        ConfigurationWorkloadDeploymentStore deployments,
        SimulatorRuntimeStore simulator,
        AlarmStore alarms,
        SourceSessionGenerationAllocator allocateSessionGeneration,
        RuntimeDefinitionBindingState definitionBinding,
        IWallClock wallClock,
        RuntimeConfigurationActivationHook? hook = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadIdentity);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        this.scopeId = scopeId;
        this.workloadIdentity = workloadIdentity;
        this.leaseDuration = leaseDuration;
        this.deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
        this.simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
        this.alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
        this.allocateSessionGeneration =
            allocateSessionGeneration ?? throw new ArgumentNullException(nameof(allocateSessionGeneration));
        this.definitionBinding =
            definitionBinding ?? throw new ArgumentNullException(nameof(definitionBinding));
        this.wallClock = wallClock ?? throw new ArgumentNullException(nameof(wallClock));
        this.hook = hook;
    }

    public async Task<Result<RuntimeConfigurationReconciliation>> PrepareNextAsync(
        CancellationToken cancellationToken = default)
    {
        var claimed = await deployments.ClaimNextAsync(
            scopeId,
            workloadIdentity,
            leaseDuration,
            cancellationToken).ConfigureAwait(false);
        if (claimed.IsFailure)
        {
            return claimed.Error?.Code.Value == "configuration.workload_job_not_available"
                ? Result.Success(new RuntimeConfigurationReconciliation(
                    RuntimeConfigurationReconciliationStatus.NoDeployment,
                    null))
                : Result.Failure<RuntimeConfigurationReconciliation>(claimed.Error!);
        }

        if (hook is not null)
        {
            await hook.InvokeAsync(
                RuntimeConfigurationActivationPoint.Claimed,
                cancellationToken).ConfigureAwait(false);
        }

        var plan = RuntimeConfigurationActivationPlanFactory.Create(claimed.Value);
        if (plan.IsFailure)
        {
            return await RejectAsync(claimed.Value, plan.Error!, cancellationToken).ConfigureAwait(false);
        }

        var received = await simulator.ReceiveAsync(
            plan.Value.Simulator,
            workloadIdentity,
            cancellationToken).ConfigureAwait(false);
        if (received.IsFailure)
        {
            return await RejectAsync(claimed.Value, received.Error!, cancellationToken).ConfigureAwait(false);
        }

        var validated = await simulator.ValidateAsync(
            scopeId,
            claimed.Value.Revision.RevisionId,
            workloadIdentity,
            cancellationToken).ConfigureAwait(false);
        if (validated.IsFailure)
        {
            return await RejectAsync(claimed.Value, validated.Error!, cancellationToken).ConfigureAwait(false);
        }

        if (validated.Value.Status == SimulatorManifestStatus.Rejected)
        {
            return await RejectAsync(
                claimed.Value,
                new OperationError(
                    ErrorCode.From(validated.Value.RejectionCode ?? "simulator.manifest_invalid"),
                    "Simulator manifest validation failed."),
                cancellationToken).ConfigureAwait(false);
        }

        var prepared = await deployments.MarkPreparedAsync(
            claimed.Value,
            cancellationToken).ConfigureAwait(false);
        if (prepared.IsFailure)
        {
            return Result.Failure<RuntimeConfigurationReconciliation>(prepared.Error!);
        }

        if (hook is not null)
        {
            await hook.InvokeAsync(
                RuntimeConfigurationActivationPoint.Prepared,
                cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(new RuntimeConfigurationReconciliation(
            RuntimeConfigurationReconciliationStatus.Prepared,
            new PreparedRuntimeConfiguration(
                plan.Value,
                prepared.Value)));
    }

    public async Task<Result<ActivatedRuntimeConfiguration>> CommitAsync(
        PreparedRuntimeConfiguration prepared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var renewed = await deployments.ConfirmLeaseAsync(
            prepared.Claim,
            leaseDuration,
            cancellationToken).ConfigureAwait(false);
        if (renewed.IsFailure)
        {
            return Result.Failure<ActivatedRuntimeConfiguration>(renewed.Error!);
        }

        var activeBefore = await simulator.ReadActiveAsync(
            scopeId,
            cancellationToken).ConfigureAwait(false);
        var expectedGeneration = activeBefore.IsSuccess
            ? activeBefore.Value.Receipt.RevisionId == renewed.Value.Revision.RevisionId
                ? activeBefore.Value.Generation
                : checked(activeBefore.Value.Generation + 1)
            : activeBefore.Error?.Code.Value == "simulator.active_not_found"
                ? 1L
                : throw new InvalidOperationException(
                    "Unable to read active Simulator generation during workload switch.");
        var epoch = RevisionNumber.From(checked((ulong)expectedGeneration));
        var alarmActivation = await alarms.ActivateDefinitionSetAsync(
            new AlarmDefinitionSet(
                RuntimeScopeId.From(scopeId.Value),
                epoch,
                prepared.Plan.AlarmDefinitions),
            cancellationToken).ConfigureAwait(false);
        if (alarmActivation.IsFailure)
        {
            return Result.Failure<ActivatedRuntimeConfiguration>(alarmActivation.Error!);
        }

        var active = await simulator.ActivateAsync(
            scopeId,
            renewed.Value.Revision.RevisionId,
            workloadIdentity,
            cancellationToken).ConfigureAwait(false);
        if (active.IsFailure)
        {
            return Result.Failure<ActivatedRuntimeConfiguration>(active.Error!);
        }

        if (active.Value.Generation != expectedGeneration)
        {
            return Failure(
                "runtime.configuration_generation_conflict",
                "Simulator generation changed during the whole-scope switch.");
        }

        var switched = await deployments.RecordSwitchAsync(
            renewed.Value,
            active.Value.Generation,
            epoch,
            cancellationToken).ConfigureAwait(false);
        if (switched.IsFailure)
        {
            return Result.Failure<ActivatedRuntimeConfiguration>(switched.Error!);
        }

        if (hook is not null)
        {
            await hook.InvokeAsync(
                RuntimeConfigurationActivationPoint.Switched,
                cancellationToken).ConfigureAwait(false);
        }

        var acknowledged = await deployments.AcknowledgeAsync(
            switched.Value,
            cancellationToken).ConfigureAwait(false);
        if (acknowledged.IsFailure)
        {
            return Result.Failure<ActivatedRuntimeConfiguration>(acknowledged.Error!);
        }

        definitionBinding.Switch(
            acknowledged.Value.Revision.RevisionId.Value,
            acknowledged.Value.AlarmDefinitionEpoch);
        var sessionGeneration = await allocateSessionGeneration(
            RuntimeScopeId.From(scopeId.Value),
            active.Value.Configuration.SourceId,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new ActivatedRuntimeConfiguration(
            acknowledged.Value.Revision.RevisionId,
            active.Value.Generation,
            acknowledged.Value.AlarmDefinitionEpoch,
            new SimulatorPollingSource(active.Value, sessionGeneration, wallClock)));
    }

    public async Task<Result<ActivatedRuntimeConfiguration>> RestoreAsync(
        CancellationToken cancellationToken = default)
    {
        var acknowledged = await deployments.ReadActivatedAsync(
            scopeId,
            cancellationToken).ConfigureAwait(false);
        if (acknowledged.IsFailure)
        {
            return Result.Failure<ActivatedRuntimeConfiguration>(acknowledged.Error!);
        }

        var active = await simulator.ReadActiveAsync(scopeId, cancellationToken).ConfigureAwait(false);
        if (active.IsFailure ||
            active.Value.Receipt.RevisionId != acknowledged.Value.Revision.RevisionId ||
            active.Value.Generation != acknowledged.Value.RuntimeGeneration)
        {
            return Failure(
                "runtime.configuration_activation_mismatch",
                "Acknowledged configuration and active runtime generation do not match.");
        }

        definitionBinding.Switch(
            acknowledged.Value.Revision.RevisionId.Value,
            acknowledged.Value.AlarmDefinitionEpoch);
        var sessionGeneration = await allocateSessionGeneration(
            RuntimeScopeId.From(scopeId.Value),
            active.Value.Configuration.SourceId,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new ActivatedRuntimeConfiguration(
            acknowledged.Value.Revision.RevisionId,
            active.Value.Generation,
            acknowledged.Value.AlarmDefinitionEpoch,
            new SimulatorPollingSource(active.Value, sessionGeneration, wallClock)));
    }

    private async Task<Result<RuntimeConfigurationReconciliation>> RejectAsync(
        ConfigurationWorkloadClaim claim,
        OperationError error,
        CancellationToken cancellationToken)
    {
        var rejected = await deployments.RejectAsync(
            claim,
            error.Code.Value,
            Sanitise(error.Code.Value),
            cancellationToken).ConfigureAwait(false);
        return rejected.IsSuccess
            ? Result.Success(new RuntimeConfigurationReconciliation(
                RuntimeConfigurationReconciliationStatus.Rejected,
                null))
            : Result.Failure<RuntimeConfigurationReconciliation>(rejected.Error!);
    }

    private static string Sanitise(string code) =>
        code switch
        {
            "runtime.configuration_fingerprint" => "Configuration fingerprints did not match.",
            "simulator.manifest_fingerprint" => "Simulator manifest fingerprint did not match.",
            _ => "Runtime configuration validation failed.",
        };

    private static Result<ActivatedRuntimeConfiguration> Failure(string code, string message) =>
        Result.Failure<ActivatedRuntimeConfiguration>(
            new OperationError(ErrorCode.From(code), message));
}
