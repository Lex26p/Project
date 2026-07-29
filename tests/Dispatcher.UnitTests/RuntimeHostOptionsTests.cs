using Dispatcher.RuntimeHost;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class RuntimeHostOptionsTests
{
    [Fact]
    public void ValidSettingsCreateExplicitBoundedPollingLimits()
    {
        var options = RuntimeHostOptions.FromSettings(
            Read(ValidSettings()));
        var limits = options.CreatePollScheduleLimits();
        var downstream = Assert.IsType<RuntimeDownstreamOptions>(
            options.Downstream);

        Assert.Equal(
            TimeSpan.FromMilliseconds(750),
            options.PollInterval);
        Assert.Equal(
            TimeSpan.FromMilliseconds(500),
            limits.Timeout);
        Assert.Equal(4, limits.MaxBindings);
        Assert.Equal(2, limits.MaxInFlight);
        Assert.Equal(
            TimeSpan.FromMilliseconds(250),
            options.ReconciliationInitialBackoff);
        Assert.Equal(
            TimeSpan.FromSeconds(4),
            options.ReconciliationMaxBackoff);
        Assert.Equal(
            "simulator_runtime",
            options.SimulatorDatabaseRole);
        Assert.Equal(
            "configuration_release",
            options.ConfigurationDatabaseRole);
        Assert.Equal(
            "equipment_runtime",
            options.EquipmentDatabaseRole);
        Assert.Equal(32, options.StagingSecretKey?.Length);
        Assert.Equal(
            TimeSpan.FromSeconds(15),
            options.DiagnosticLeaseDuration);
        Assert.Equal(
            TimeSpan.FromMilliseconds(200),
            options.DiagnosticPollInterval);
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            options.DeploymentLeaseDuration);
        Assert.Equal(
            TimeSpan.FromMilliseconds(500),
            options.ConfigurationReconciliationInterval);
        Assert.Equal(12, options.SnmpLimits.MaxPoints);
        Assert.Equal(24, options.SnmpLimits.MaxOidArcs);
        Assert.Equal(96, options.SnmpLimits.MaxOidBytes);
        Assert.Equal(48, options.SnmpWireLimits.MaxCommunityBytes);
        Assert.Equal(2048, options.SnmpWireLimits.MaxRequestBytes);
        Assert.Equal("history", downstream.HistoryDatabaseRole);
        Assert.Equal("alarm", downstream.AlarmDatabaseRole);
        Assert.Equal(
            "event_journal",
            downstream.EventDatabaseRole);
        Assert.Equal(2, downstream.RetryPolicy.MaxAttempts);
        Assert.Equal(
            TimeSpan.FromMilliseconds(50),
            downstream.RetryPolicy.InitialBackoff);
        Assert.Equal(
            TimeSpan.FromMilliseconds(500),
            downstream.RetryPolicy.MaximumBackoff);
        Assert.Equal(64, downstream.EventMaxFeedChanges);
    }

    [Theory]
    [InlineData(
        "DISPATCHER_RUNTIME_SCHEDULER_MAX_IN_FLIGHT",
        "5")]
    [InlineData(
        "DISPATCHER_RUNTIME_RECONCILIATION_INITIAL_BACKOFF_MS",
        "5000")]
    public void InvalidHostRelationshipsAreRejected(
        string name,
        string value)
    {
        var settings = ValidSettings();
        settings[name] = value;

        Assert.Throws<InvalidOperationException>(() =>
            RuntimeHostOptions.FromSettings(
                Read(settings)));
    }

    [Theory]
    [InlineData(
        "DISPATCHER_RUNTIME_EVENT_MAX_FEED_CHANGES",
        "129")]
    [InlineData(
        "DISPATCHER_RUNTIME_DOWNSTREAM_INITIAL_BACKOFF_MS",
        "1000")]
    public void InvalidDownstreamRelationshipsAreRejected(
        string name,
        string value)
    {
        var settings = ValidSettings();
        settings[name] = value;

        Assert.ThrowsAny<ArgumentException>(() =>
            RuntimeHostOptions.FromSettings(
                Read(settings)));
    }

    private static Dictionary<string, string> ValidSettings() =>
        new(StringComparer.Ordinal)
        {
            ["DISPATCHER_RUNTIME_SCOPE_ID"] =
                "b1000000-0000-0000-0000-000000000001",
            ["DISPATCHER_RUNTIME_WORKLOAD_IDENTITY"] =
                "runtime-host",
            ["DISPATCHER_RUNTIME_CONNECTION_STRING"] =
                "Host=localhost;Database=dispatcher",
            ["DISPATCHER_RUNTIME_DATABASE_ROLE"] =
                "core_runtime",
            ["DISPATCHER_RUNTIME_SIMULATOR_DATABASE_ROLE"] =
                "simulator_runtime",
            ["DISPATCHER_RUNTIME_CONFIGURATION_DATABASE_ROLE"] =
                "configuration_release",
            ["DISPATCHER_RUNTIME_EQUIPMENT_DATABASE_ROLE"] =
                "equipment_runtime",
            ["DISPATCHER_RUNTIME_STAGING_SECRET_KEY"] =
                "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
            ["DISPATCHER_RUNTIME_DIAGNOSTIC_LEASE_MS"] =
                "15000",
            ["DISPATCHER_RUNTIME_DIAGNOSTIC_POLL_MS"] =
                "200",
            ["DISPATCHER_RUNTIME_DEPLOYMENT_LEASE_MS"] =
                "30000",
            ["DISPATCHER_RUNTIME_CONFIGURATION_RECONCILIATION_MS"] =
                "500",
            ["DISPATCHER_RUNTIME_MAX_CURRENT_POINTS"] =
                "128",
            ["DISPATCHER_RUNTIME_RETAINED_CURRENT_CHANGES"] =
                "512",
            ["DISPATCHER_RUNTIME_INGRESS_CAPACITY"] =
                "32",
            ["DISPATCHER_RUNTIME_MAX_PROTOCOL_SOURCES"] =
                "4",
            ["DISPATCHER_RUNTIME_POLL_INTERVAL_MS"] =
                "750",
            ["DISPATCHER_RUNTIME_POLL_TIMEOUT_MS"] =
                "500",
            ["DISPATCHER_RUNTIME_SCHEDULER_MAX_BINDINGS"] =
                "4",
            ["DISPATCHER_RUNTIME_SCHEDULER_MAX_IN_FLIGHT"] =
                "2",
            ["DISPATCHER_RUNTIME_RECONCILIATION_INITIAL_BACKOFF_MS"] =
                "250",
            ["DISPATCHER_RUNTIME_RECONCILIATION_MAX_BACKOFF_MS"] =
                "4000",
            ["DISPATCHER_RUNTIME_SNMP_MAX_POINTS"] =
                "12",
            ["DISPATCHER_RUNTIME_SNMP_MAX_OID_ARCS"] =
                "24",
            ["DISPATCHER_RUNTIME_SNMP_MAX_OID_BYTES"] =
                "96",
            ["DISPATCHER_RUNTIME_SNMP_MAX_COMMUNITY_BYTES"] =
                "48",
            ["DISPATCHER_RUNTIME_SNMP_MAX_REQUEST_BYTES"] =
                "2048",
            ["DISPATCHER_RUNTIME_HISTORY_DATABASE_ROLE"] =
                "history",
            ["DISPATCHER_RUNTIME_ALARM_DATABASE_ROLE"] =
                "alarm",
            ["DISPATCHER_RUNTIME_EVENT_DATABASE_ROLE"] =
                "event_journal",
            ["DISPATCHER_RUNTIME_CONFIGURATION_REVISION_ID"] =
                "b2000000-0000-0000-0000-000000000001",
            ["DISPATCHER_RUNTIME_ALARM_DEFINITION_EPOCH"] =
                "1",
            ["DISPATCHER_RUNTIME_HISTORY_MAX_PAGE_SIZE"] =
                "100",
            ["DISPATCHER_RUNTIME_HISTORY_MAX_AGGREGATE_BUCKETS"] =
                "100",
            ["DISPATCHER_RUNTIME_EVENT_MAX_PAGE_SIZE"] =
                "100",
            ["DISPATCHER_RUNTIME_EVENT_RETAINED_PROJECTION_CHANGES"] =
                "128",
            ["DISPATCHER_RUNTIME_EVENT_MAX_FEED_CHANGES"] =
                "64",
            ["DISPATCHER_RUNTIME_DOWNSTREAM_MAX_ATTEMPTS"] =
                "2",
            ["DISPATCHER_RUNTIME_DOWNSTREAM_INITIAL_BACKOFF_MS"] =
                "50",
            ["DISPATCHER_RUNTIME_DOWNSTREAM_MAX_BACKOFF_MS"] =
                "500",
        };

    private static Func<string, string?> Read(
        Dictionary<string, string> settings) =>
        name => settings.TryGetValue(
            name,
            out var value)
                ? value
                : null;
}
