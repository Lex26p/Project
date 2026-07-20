using System.Text.Json;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Protocols;
using Dispatcher.RuntimeHost;
using Dispatcher.Semantics;
using Dispatcher.Simulator;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class ProtocolRuntimeContractTests
{
    private const string RawSecret = "raw-secret-must-never-escape";
    private static readonly DateTimeOffset Start = new(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly RuntimeScopeId ScopeId = RuntimeScopeId.From(
        Guid.Parse("a1000000-0000-0000-0000-000000000001"));
    private static readonly SourceId SourceId = SourceId.From(
        Guid.Parse("a2000000-0000-0000-0000-000000000001"));
    private static readonly PointId PointId = PointId.From(
        Guid.Parse("a3000000-0000-0000-0000-000000000001"));
    private static readonly ProtocolWorkloadIdentity Workload = ProtocolWorkloadIdentity.From(
        "dispatcher-runtime:windows-x64");

    [Fact]
    public async Task ProtocolSourceContractHasParityWithSimulatorSemantics()
    {
        var clock = new FixedClock(Start);
        var simulator = CreateSimulator(clock);
        var attempt = new PollAttemptToken(simulator.Binding, 1, clock.GetTimestamp());
        var expected = simulator.Poll(attempt);
        using var controller = Controller(
            new StaticTransport(new byte[] { 0x01 }),
            new StaticParser(expected),
            maxResponseBytes: 16,
            maxObservations: 4);

        var acquired = await controller.AcquireAsync(new ProtocolSourceRequest(simulator.Binding, 1, null));

        Assert.True(acquired.IsSuccess);
        Assert.Equal(simulator.Binding, acquired.Value.Binding);
        Assert.Equal((ulong)1, acquired.Value.ScheduleSequence);
        Assert.True(expected.SequenceEqual(acquired.Value.Observations));
    }

    [Fact]
    public async Task HostilePayloadAndObservationExpansionAreBoundedBeforeCore()
    {
        var binding = Binding();
        var parser = new CountingParser([Observation(binding, 1), Observation(binding, 2)]);
        using var oversized = Controller(
            new StaticTransport(new byte[] { 0, 1, 2, 3, 4 }),
            parser,
            maxResponseBytes: 4,
            maxObservations: 1);

        var rejectedBytes = await oversized.AcquireAsync(new ProtocolSourceRequest(binding, 1, null));

        Assert.Equal("protocol.response_limit", rejectedBytes.Error?.Code.Value);
        Assert.Equal(0, parser.ParseCount);

        using var expanded = Controller(
            new StaticTransport(new byte[] { 0 }),
            parser,
            maxResponseBytes: 4,
            maxObservations: 1);
        var rejectedObservations = await expanded.AcquireAsync(new ProtocolSourceRequest(binding, 1, null));
        Assert.Equal("protocol.observation_limit", rejectedObservations.Error?.Code.Value);
    }

    [Fact]
    public async Task RawSecretIsLocalToLeaseAndAbsentFromSerializableContracts()
    {
        var transport = new SecretCheckingTransport();
        using var controller = Controller(transport, new StaticParser([Observation(Binding(), 1)]));

        var result = await controller.AcquireAsync(new ProtocolSourceRequest(
            Binding(),
            1,
            ProtocolSecretReference.From("env:SOURCE_SECRET")));

        Assert.True(result.IsSuccess);
        Assert.True(transport.SecretWasAvailableInsideRead);
        Assert.Equal("[REDACTED]", transport.CapturedRequest!.Secret!.ToString());
        var serialized = JsonSerializer.Serialize(transport.CapturedRequest);
        Assert.DoesNotContain(RawSecret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(RawSecret, JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiagnosticIsReadOnlyAndDoesNotOwnCurrentOrAlarm()
    {
        var binding = Binding();
        var clock = new FixedClock(Start);
        var current = new CoreRuntime(ScopeId, clock, clock, new RuntimeCurrentLimits(4, 8));
        Assert.True(current.ActivateBinding(binding).IsSuccess);
        using var controller = Controller(new StaticTransport(new byte[] { 1, 2 }), new ThrowingParser());

        var diagnostic = await controller.DiagnoseAsync(new ProtocolDiagnosticRequest(binding, null));

        Assert.True(diagnostic.IsSuccess);
        Assert.Equal(ProtocolDiagnosticStatus.Reachable, diagnostic.Value.Status);
        Assert.Empty(current.GetSnapshot().Entries);
        Assert.DoesNotContain(
            typeof(IReadOnlyProtocolTransport).GetMethods(),
            method => method.Name.Contains("Write", StringComparison.OrdinalIgnoreCase) ||
                      method.Name.Contains("Command", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(ProtocolSourceController).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name is "Dispatcher.Alarm" or "Dispatcher.Server");
    }

    [Fact]
    public async Task LifecycleStopClosesAdmissionAndDrainsInFlightRead()
    {
        var binding = Binding();
        var transport = new BlockingTransport();
        using var controller = Controller(transport, new StaticParser([Observation(binding, 1)]));
        var supervisor = new ProtocolRuntimeSupervisor(Workload, maxSources: 1);
        Assert.True(supervisor.Register(SourceId, controller).IsSuccess);
        Assert.True(supervisor.ActivateBinding(binding).IsSuccess);
        Assert.True(supervisor.Start().IsSuccess);
        var active = supervisor.AcquireAsync(new ProtocolSourceRequest(binding, 1, null));
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var stop = supervisor.StopAsync();
        Assert.Equal(ProtocolSupervisorState.Draining, supervisor.GetSnapshot().State);
        var denied = await supervisor.DiagnoseAsync(new ProtocolDiagnosticRequest(binding, null));
        Assert.Equal("protocol.not_accepting", denied.Error?.Code.Value);
        Assert.False(stop.IsCompleted);

        transport.Release.TrySetResult(new byte[] { 0x01 });
        Assert.True((await active).IsSuccess);
        Assert.True((await stop).IsSuccess);
        Assert.Equal(ProtocolSupervisorState.Stopped, supervisor.GetSnapshot().State);
    }

    [Fact]
    public void RuntimeHostIsASeparateNonAspNetProcessBoundary()
    {
        var hostAssembly = typeof(RuntimeProcess).Assembly;
        Assert.Equal("Dispatcher.RuntimeHost", hostAssembly.GetName().Name);
        Assert.DoesNotContain(
            hostAssembly.GetReferencedAssemblies(),
            assembly => assembly.Name is "Dispatcher.Server" or "Microsoft.AspNetCore");
        Assert.DoesNotContain(
            typeof(ProtocolRuntimeSupervisor).GetMethods(),
            method => method.Name.Contains("Alarm", StringComparison.OrdinalIgnoreCase) ||
                      method.Name.Contains("Command", StringComparison.OrdinalIgnoreCase));
    }

    private static ProtocolSourceController Controller(
        IReadOnlyProtocolTransport transport,
        IProtocolObservationParser parser,
        int maxResponseBytes = 16,
        int maxObservations = 4) =>
        new(
            Workload,
            transport,
            parser,
            new StaticSecretResolver(),
            new ProtocolIoLimits(TimeSpan.FromSeconds(1), maxResponseBytes, maxObservations, 1));

    private static SourceBinding Binding() => new(
        ScopeId,
        SourceId,
        SourceBindingGeneration.From(1),
        SourceSessionGeneration.From(1));

    private static SourceObservation Observation(SourceBinding binding, ulong position) => new(
        binding.ScopeId,
        binding.SourceId,
        PointId.From(Guid.Parse(position == 1
            ? "a3000000-0000-0000-0000-000000000001"
            : "a3000000-0000-0000-0000-000000000002")),
        new OwnerPosition<SourceObservation>(position),
        TypedValue.From(10L + checked((long)position)),
        Unit.FromSymbol("kW"),
        DataQuality.Good,
        Freshness.Fresh,
        SourceTimestamp.FromUtc(Start.AddSeconds(checked((long)position))));

    private static SimulatorPollingSource CreateSimulator(FixedClock clock)
    {
        var config = new SimulatorScenarioConfig(
            ScopeId,
            SourceId,
            23,
            [new SimulatorPointConfig(PointId, 100, 0, Unit.FromSymbol("kW"))]);
        var receipt = new SimulatorManifestReceipt(
            ConfigurationRevisionId.New(),
            Dispatcher.Facilities.FacilityScopeId.From(ScopeId.Value),
            RevisionNumber.Initial,
            null,
            new string('A', 64),
            new string('B', 64),
            SimulatorManifestStatus.Activated,
            null,
            null,
            Start,
            Start,
            Start);
        return new SimulatorPollingSource(
            new ActiveSimulatorManifest(receipt, config, 1),
            SourceSessionGeneration.From(1),
            clock);
    }

    private sealed class FixedClock : IWallClock, IMonotonicClock
    {
        private readonly DateTimeOffset time;

        public FixedClock(DateTimeOffset time) => this.time = time;

        public DateTimeOffset GetUtcNow() => time;
        public MonotonicTimestamp GetTimestamp() => new(0);
        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) => TimeSpan.Zero;
    }

    private sealed class StaticTransport : IReadOnlyProtocolTransport
    {
        private readonly ReadOnlyMemory<byte> response;

        public StaticTransport(ReadOnlyMemory<byte> response) => this.response = response;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(
            ProtocolTransportRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(response);
    }

    private sealed class BlockingTransport : IReadOnlyProtocolTransport
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ReadOnlyMemory<byte>> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(
            ProtocolTransportRequest request,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class SecretCheckingTransport : IReadOnlyProtocolTransport
    {
        public ProtocolTransportRequest? CapturedRequest { get; private set; }
        public bool SecretWasAvailableInsideRead { get; private set; }

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(
            ProtocolTransportRequest request,
            CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            SecretWasAvailableInsideRead = await request.Secret!.UseAsync(
                (secret, _) => ValueTask.FromResult(secret.Span.SequenceEqual(RawSecret.AsSpan())),
                cancellationToken);
            return new byte[] { 1 };
        }
    }

    private sealed class StaticParser : IProtocolObservationParser
    {
        private readonly IReadOnlyList<SourceObservation> observations;

        public StaticParser(IReadOnlyList<SourceObservation> observations) => this.observations = observations;

        public Result<IReadOnlyList<SourceObservation>> Parse(
            SourceBinding binding,
            ReadOnlyMemory<byte> response) => Result.Success(observations);
    }

    private sealed class CountingParser : IProtocolObservationParser
    {
        private readonly IReadOnlyList<SourceObservation> observations;

        public CountingParser(IReadOnlyList<SourceObservation> observations) => this.observations = observations;

        public int ParseCount { get; private set; }

        public Result<IReadOnlyList<SourceObservation>> Parse(
            SourceBinding binding,
            ReadOnlyMemory<byte> response)
        {
            ParseCount++;
            return Result.Success(observations);
        }
    }

    private sealed class ThrowingParser : IProtocolObservationParser
    {
        public Result<IReadOnlyList<SourceObservation>> Parse(
            SourceBinding binding,
            ReadOnlyMemory<byte> response) => throw new InvalidOperationException("Diagnostics must not parse acquisition data.");
    }

    private sealed class StaticSecretResolver : IProtocolSecretResolver
    {
        public ValueTask<ProtocolSecretLease> ResolveAsync(
            ProtocolSecretReference reference,
            ProtocolWorkloadIdentity workloadIdentity,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ProtocolSecretLease.Create(RawSecret));
    }
}
