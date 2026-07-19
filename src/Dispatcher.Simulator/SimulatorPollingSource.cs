using Dispatcher.Core;
using Dispatcher.Semantics;

namespace Dispatcher.Simulator;

public sealed class SimulatorPollingSource
{
    private readonly SimulatorScenario scenario;

    public SimulatorPollingSource(
        ActiveSimulatorManifest activeManifest,
        SourceSessionGeneration sessionGeneration,
        IWallClock wallClock)
    {
        ArgumentNullException.ThrowIfNull(activeManifest);
        ArgumentNullException.ThrowIfNull(wallClock);
        Binding = new SourceBinding(
            activeManifest.Configuration.ScopeId,
            activeManifest.Configuration.SourceId,
            SourceBindingGeneration.From(checked((ulong)activeManifest.Generation)),
            sessionGeneration);
        scenario = new SimulatorScenario(activeManifest.Configuration, wallClock);
    }

    public SourceBinding Binding { get; }

    public IReadOnlyList<SourceObservation> Poll(PollAttemptToken attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Binding != Binding)
        {
            throw new ArgumentException("Poll attempt belongs to another Simulator source session.", nameof(attempt));
        }

        return scenario.NextStep();
    }
}
