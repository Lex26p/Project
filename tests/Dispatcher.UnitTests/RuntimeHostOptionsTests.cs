using Dispatcher.RuntimeHost;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class RuntimeHostOptionsTests
{
    [Fact]
    public void ValidSettingsCreateExplicitBoundedPollingLimits()
    {
        var options = RuntimeHostOptions.FromSettings(Read(ValidSettings()));
        var limits = options.CreatePollScheduleLimits();

        Assert.Equal(TimeSpan.FromMilliseconds(750), options.PollInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(500), limits.Timeout);
        Assert.Equal(4, limits.MaxBindings);
        Assert.Equal(2, limits.MaxInFlight);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.ReconciliationInitialBackoff);
        Assert.Equal(TimeSpan.FromSeconds(4), options.ReconciliationMaxBackoff);
        Assert.Equal("simulator_runtime", options.SimulatorDatabaseRole);
    }

    [Theory]
    [InlineData("DISPATCHER_RUNTIME_SCHEDULER_MAX_IN_FLIGHT", "5")]
    [InlineData("DISPATCHER_RUNTIME_RECONCILIATION_INITIAL_BACKOFF_MS", "5000")]
    public void InvalidBoundedRelationshipsAreRejected(string name, string value)
    {
        var settings = ValidSettings();
        settings[name] = value;

        Assert.Throws<InvalidOperationException>(() =>
            RuntimeHostOptions.FromSettings(Read(settings)));
    }

    private static Dictionary<string, string> ValidSettings() => new(StringComparer.Ordinal)
    {
        ["DISPATCHER_RUNTIME_SCOPE_ID"] = "b1000000-0000-0000-0000-000000000001",
        ["DISPATCHER_RUNTIME_WORKLOAD_IDENTITY"] = "runtime-host",
        ["DISPATCHER_RUNTIME_CONNECTION_STRING"] = "Host=localhost;Database=dispatcher",
        ["DISPATCHER_RUNTIME_DATABASE_ROLE"] = "core_runtime",
        ["DISPATCHER_RUNTIME_SIMULATOR_DATABASE_ROLE"] = "simulator_runtime",
        ["DISPATCHER_RUNTIME_MAX_CURRENT_POINTS"] = "128",
        ["DISPATCHER_RUNTIME_RETAINED_CURRENT_CHANGES"] = "512",
        ["DISPATCHER_RUNTIME_INGRESS_CAPACITY"] = "32",
        ["DISPATCHER_RUNTIME_MAX_PROTOCOL_SOURCES"] = "4",
        ["DISPATCHER_RUNTIME_POLL_INTERVAL_MS"] = "750",
        ["DISPATCHER_RUNTIME_POLL_TIMEOUT_MS"] = "500",
        ["DISPATCHER_RUNTIME_SCHEDULER_MAX_BINDINGS"] = "4",
        ["DISPATCHER_RUNTIME_SCHEDULER_MAX_IN_FLIGHT"] = "2",
        ["DISPATCHER_RUNTIME_RECONCILIATION_INITIAL_BACKOFF_MS"] = "250",
        ["DISPATCHER_RUNTIME_RECONCILIATION_MAX_BACKOFF_MS"] = "4000",
    };

    private static Func<string, string?> Read(Dictionary<string, string> settings) =>
        name => settings.TryGetValue(name, out var value) ? value : null;
}
