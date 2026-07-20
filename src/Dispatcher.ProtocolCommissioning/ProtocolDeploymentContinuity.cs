using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Semantics;

namespace Dispatcher.ProtocolCommissioning;

public enum ProtocolDeploymentState
{
    Prepared = 1,
    Active = 2,
    DegradedProcessUnavailable = 3,
}

public sealed record ProtocolDeploymentSnapshot(
    RuntimeScopeId ScopeId,
    ConfigurationRevisionId RevisionId,
    ProtocolDeploymentState State,
    SourceSessionGeneration? SessionGeneration);

public sealed class ProtocolDeploymentContinuity
{
    private readonly object sync = new();
    private readonly ProtocolActivationPlan plan;
    private ProtocolDeploymentState state = ProtocolDeploymentState.Prepared;
    private SourceSessionGeneration? sessionGeneration;

    public ProtocolDeploymentContinuity(ProtocolActivationPlan plan) =>
        this.plan = plan ?? throw new ArgumentNullException(nameof(plan));

    public Result<IReadOnlyList<SourceBinding>> Activate(SourceSessionGeneration nextSessionGeneration)
    {
        lock (sync)
        {
            if (sessionGeneration is { } active && nextSessionGeneration.Value <= active.Value)
            {
                return Result.Failure<IReadOnlyList<SourceBinding>>(new OperationError(
                    ErrorCode.From("protocol.session_stale"),
                    "Recovered protocol process must use a newer session generation."));
            }

            sessionGeneration = nextSessionGeneration;
            state = ProtocolDeploymentState.Active;
            return Result.Success(plan.CreateBindings(nextSessionGeneration));
        }
    }

    public Result MarkProcessUnavailable(SourceSessionGeneration failedSessionGeneration)
    {
        lock (sync)
        {
            if (state != ProtocolDeploymentState.Active || sessionGeneration != failedSessionGeneration)
            {
                return Result.Failure(new OperationError(
                    ErrorCode.From("protocol.session_stale"),
                    "Only the active protocol process session can degrade the deployment."));
            }

            state = ProtocolDeploymentState.DegradedProcessUnavailable;
            return Result.Success();
        }
    }

    public ProtocolDeploymentSnapshot GetSnapshot()
    {
        lock (sync)
        {
            return new ProtocolDeploymentSnapshot(plan.ScopeId, plan.RevisionId, state, sessionGeneration);
        }
    }
}
