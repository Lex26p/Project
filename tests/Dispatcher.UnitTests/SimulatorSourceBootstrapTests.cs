using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Facilities;
using Dispatcher.RuntimeHost;
using Dispatcher.Semantics;
using Dispatcher.Simulator;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class SimulatorSourceBootstrapTests
{
    private static readonly RuntimeScopeId ScopeId = RuntimeScopeId.From(
        Guid.Parse("b2000000-0000-0000-0000-000000000001"));
    private static readonly SourceId SourceId = Dispatcher.Core.SourceId.From(
        Guid.Parse("b3000000-0000-0000-0000-000000000001"));

    [Fact]
    public async Task MissingActiveManifestIsExpectedAndDoesNotAllocateSession()
    {
        var allocationCalled = false;
        var bootstrap = new SimulatorSourceBootstrap(
            ScopeId,
            (_, _) => Task.FromResult(Result.Failure<ActiveSimulatorManifest>(
                new OperationError(
                    ErrorCode.From("simulator.active_not_found"),
                    "No active Simulator manifest exists."))),
            (_, _, _) =>
            {
                allocationCalled = true;
                return Task.FromResult(SourceSessionGeneration.From(1));
            },
            SystemClock.Instance);

        var result = await bootstrap.ReconcileAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(SimulatorSourceReconciliationStatus.NoActiveManifest, result.Value.Status);
        Assert.Null(result.Value.Source);
        Assert.False(allocationCalled);
    }

    [Fact]
    public async Task NewSessionFencesCompletionFromPreviousBootstrap()
    {
        var active = CreateActiveManifest();
        ulong generation = 0;
        var bootstrap = new SimulatorSourceBootstrap(
            ScopeId,
            (_, _) => Task.FromResult(Result.Success(active)),
            (_, _, _) => Task.FromResult(SourceSessionGeneration.From(checked(++generation))),
            SystemClock.Instance);
        var scheduler = new BoundedPollScheduler(
            ScopeId,
            new PollScheduleLimits(TimeSpan.FromSeconds(30), maxBindings: 1, maxInFlight: 1),
            SystemClock.Instance);

        var first = (await bootstrap.ReconcileAsync()).Value.Source!;
        Assert.True(scheduler.ActivateBinding(first.Binding).IsSuccess);
        var firstAttempt = scheduler.TryStart(first.Binding, 1).Value.Attempt!;
        var observations = first.Poll(firstAttempt);

        var restarted = (await bootstrap.ReconcileAsync()).Value.Source!;
        Assert.Equal(first.Binding.ScopeId, restarted.Binding.ScopeId);
        Assert.Equal(first.Binding.SourceId, restarted.Binding.SourceId);
        Assert.Equal(first.Binding.BindingGeneration, restarted.Binding.BindingGeneration);
        Assert.True(restarted.Binding.SessionGeneration.Value > first.Binding.SessionGeneration.Value);
        Assert.Equal((ulong)2, restarted.Binding.SessionGeneration.Value);
        Assert.True(scheduler.ActivateBinding(restarted.Binding).IsSuccess);

        var stale = scheduler.Complete(firstAttempt, observations);
        Assert.Equal(PollCompletionStatus.Stale, stale.Value.Status);
        Assert.Null(stale.Value.Cut);
    }

    private static ActiveSimulatorManifest CreateActiveManifest()
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
            new DateTimeOffset(2026, 7, 26, 20, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 26, 20, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 26, 20, 2, 0, TimeSpan.Zero));
        var configuration = new SimulatorScenarioConfig(
            ScopeId,
            SourceId,
            42,
            [
                new SimulatorPointConfig(
                    PointId.From(Guid.Parse("b4000000-0000-0000-0000-000000000001")),
                    10,
                    0,
                    Unit.FromSymbol("kW")),
            ]);
        return new ActiveSimulatorManifest(receipt, configuration, Generation: 7);
    }
}
