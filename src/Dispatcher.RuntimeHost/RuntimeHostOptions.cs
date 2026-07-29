using System.Globalization;
using Dispatcher.Core;
using Dispatcher.Modbus;
using Dispatcher.Protocols;
using Dispatcher.Semantics;
using Dispatcher.Snmp;

namespace Dispatcher.RuntimeHost;

public sealed record RuntimeDownstreamOptions
{
    public RuntimeDownstreamOptions(
        string historyDatabaseRole,
        string alarmDatabaseRole,
        string eventDatabaseRole,
        Guid configurationRevisionId,
        RevisionNumber alarmDefinitionEpoch,
        int historyMaxPageSize,
        int historyMaxAggregateBuckets,
        int eventMaxPageSize,
        int eventRetainedProjectionChanges,
        int eventMaxFeedChanges,
        RuntimeDownstreamRetryPolicy retryPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyDatabaseRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(alarmDatabaseRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventDatabaseRole);
        ArgumentOutOfRangeException.ThrowIfEqual(
            configurationRevisionId,
            Guid.Empty);

        if (!alarmDefinitionEpoch.IsDefined)
        {
            throw new ArgumentOutOfRangeException(
                nameof(alarmDefinitionEpoch));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            historyMaxPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            historyMaxAggregateBuckets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            eventMaxPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            eventRetainedProjectionChanges);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            eventMaxFeedChanges);
        if (eventMaxFeedChanges > eventRetainedProjectionChanges)
        {
            throw new ArgumentOutOfRangeException(
                nameof(eventMaxFeedChanges),
                "Event feed batch size cannot exceed retained projection changes.");
        }

        ArgumentNullException.ThrowIfNull(retryPolicy);
        HistoryDatabaseRole = historyDatabaseRole;
        AlarmDatabaseRole = alarmDatabaseRole;
        EventDatabaseRole = eventDatabaseRole;
        ConfigurationRevisionId = configurationRevisionId;
        AlarmDefinitionEpoch = alarmDefinitionEpoch;
        HistoryMaxPageSize = historyMaxPageSize;
        HistoryMaxAggregateBuckets = historyMaxAggregateBuckets;
        EventMaxPageSize = eventMaxPageSize;
        EventRetainedProjectionChanges =
            eventRetainedProjectionChanges;
        EventMaxFeedChanges = eventMaxFeedChanges;
        RetryPolicy = retryPolicy;
    }

    public string HistoryDatabaseRole { get; }

    public string AlarmDatabaseRole { get; }

    public string EventDatabaseRole { get; }

    public Guid ConfigurationRevisionId { get; }

    public RevisionNumber AlarmDefinitionEpoch { get; }

    public int HistoryMaxPageSize { get; }

    public int HistoryMaxAggregateBuckets { get; }

    public int EventMaxPageSize { get; }

    public int EventRetainedProjectionChanges { get; }

    public int EventMaxFeedChanges { get; }

    public RuntimeDownstreamRetryPolicy RetryPolicy { get; }
}

public sealed record RuntimeHostOptions(
    RuntimeScopeId ScopeId,
    ProtocolWorkloadIdentity WorkloadIdentity,
    string ConnectionString,
    string DatabaseRole,
    string SimulatorDatabaseRole,
    int MaxCurrentPoints,
    int RetainedCurrentChanges,
    int IngressCapacity,
    int MaxProtocolSources,
    TimeSpan PollInterval,
    TimeSpan PollTimeout,
    int SchedulerMaxBindings,
    int SchedulerMaxInFlight,
    TimeSpan ReconciliationInitialBackoff,
    TimeSpan ReconciliationMaxBackoff)
{
    public RuntimeDownstreamOptions? Downstream { get; init; }

    public string? ConfigurationDatabaseRole { get; init; }

    public string? EquipmentDatabaseRole { get; init; }

    public byte[]? StagingSecretKey { get; init; }

    public TimeSpan DiagnosticLeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan DiagnosticPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan DeploymentLeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ConfigurationReconciliationInterval { get; init; } = TimeSpan.FromSeconds(1);

    public ModbusConfigurationLimits ModbusLimits { get; init; } = new(256, 512);

    public SnmpConfigurationLimits SnmpLimits { get; init; } = new(256, 128, 512);

    public SnmpWireLimits SnmpWireLimits { get; init; } = new(128, 4096);

    public int ProtocolMaxResponseBytes { get; init; } = 65_536;

    public int ProtocolMaxObservations { get; init; } = 256;

    public int ProtocolMaxConcurrentOperations { get; init; } = 1;

    public PollScheduleLimits CreatePollScheduleLimits() =>
        new(PollTimeout, SchedulerMaxBindings, SchedulerMaxInFlight);

    public ProtocolIoLimits CreateProtocolIoLimits() =>
        new(
            PollTimeout,
            ProtocolMaxResponseBytes,
            ProtocolMaxObservations,
            ProtocolMaxConcurrentOperations);

    public static RuntimeHostOptions FromEnvironment() =>
        FromSettings(Environment.GetEnvironmentVariable);

    public static RuntimeHostOptions FromSettings(
        Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var schedulerMaxBindings = PositiveInt(
            read,
            "DISPATCHER_RUNTIME_SCHEDULER_MAX_BINDINGS");
        var schedulerMaxInFlight = PositiveInt(
            read,
            "DISPATCHER_RUNTIME_SCHEDULER_MAX_IN_FLIGHT");
        if (schedulerMaxInFlight > schedulerMaxBindings)
        {
            throw new InvalidOperationException(
                "Runtime scheduler max in-flight must not exceed max bindings.");
        }

        var reconciliationInitialBackoff = PositiveMilliseconds(
            read,
            "DISPATCHER_RUNTIME_RECONCILIATION_INITIAL_BACKOFF_MS");
        var reconciliationMaxBackoff = PositiveMilliseconds(
            read,
            "DISPATCHER_RUNTIME_RECONCILIATION_MAX_BACKOFF_MS");
        if (reconciliationInitialBackoff > reconciliationMaxBackoff)
        {
            throw new InvalidOperationException(
                "Runtime reconciliation initial backoff must not exceed maximum backoff.");
        }

        var downstreamInitialBackoff = PositiveMilliseconds(
            read,
            "DISPATCHER_RUNTIME_DOWNSTREAM_INITIAL_BACKOFF_MS");
        var downstreamMaxBackoff = PositiveMilliseconds(
            read,
            "DISPATCHER_RUNTIME_DOWNSTREAM_MAX_BACKOFF_MS");
        var downstream = new RuntimeDownstreamOptions(
            Required(
                read,
                "DISPATCHER_RUNTIME_HISTORY_DATABASE_ROLE"),
            Required(
                read,
                "DISPATCHER_RUNTIME_ALARM_DATABASE_ROLE"),
            Required(
                read,
                "DISPATCHER_RUNTIME_EVENT_DATABASE_ROLE"),
            Guid.Parse(
                Required(
                    read,
                    "DISPATCHER_RUNTIME_CONFIGURATION_REVISION_ID")),
            RevisionNumber.From(
                PositiveUlong(
                    read,
                    "DISPATCHER_RUNTIME_ALARM_DEFINITION_EPOCH")),
            PositiveInt(
                read,
                "DISPATCHER_RUNTIME_HISTORY_MAX_PAGE_SIZE"),
            PositiveInt(
                read,
                "DISPATCHER_RUNTIME_HISTORY_MAX_AGGREGATE_BUCKETS"),
            PositiveInt(
                read,
                "DISPATCHER_RUNTIME_EVENT_MAX_PAGE_SIZE"),
            PositiveInt(
                read,
                "DISPATCHER_RUNTIME_EVENT_RETAINED_PROJECTION_CHANGES"),
            PositiveInt(
                read,
                "DISPATCHER_RUNTIME_EVENT_MAX_FEED_CHANGES"),
            new RuntimeDownstreamRetryPolicy(
                PositiveInt(
                    read,
                    "DISPATCHER_RUNTIME_DOWNSTREAM_MAX_ATTEMPTS"),
                downstreamInitialBackoff,
                downstreamMaxBackoff));

        return new RuntimeHostOptions(
            RuntimeScopeId.From(
                Guid.Parse(
                    Required(
                        read,
                        "DISPATCHER_RUNTIME_SCOPE_ID"))),
            ProtocolWorkloadIdentity.From(
                Required(
                    read,
                    "DISPATCHER_RUNTIME_WORKLOAD_IDENTITY")),
            Required(
                read,
                "DISPATCHER_RUNTIME_CONNECTION_STRING"),
            Required(
                read,
                "DISPATCHER_RUNTIME_DATABASE_ROLE"),
            Required(
                read,
                "DISPATCHER_RUNTIME_SIMULATOR_DATABASE_ROLE"),
            PositiveInt(
                read,
                "DISPATCHER_RUNTIME_MAX_CURRENT_POINTS"),
            PositiveInt(
                read,
                "DISPATCHER_RUNTIME_RETAINED_CURRENT_CHANGES"),
            PositiveInt(
                read,
                "DISPATCHER_RUNTIME_INGRESS_CAPACITY"),
            PositiveInt(
                read,
                "DISPATCHER_RUNTIME_MAX_PROTOCOL_SOURCES"),
            PositiveMilliseconds(
                read,
                "DISPATCHER_RUNTIME_POLL_INTERVAL_MS"),
            PositiveMilliseconds(
                read,
                "DISPATCHER_RUNTIME_POLL_TIMEOUT_MS"),
            schedulerMaxBindings,
            schedulerMaxInFlight,
            reconciliationInitialBackoff,
            reconciliationMaxBackoff)
        {
            Downstream = downstream,
            ConfigurationDatabaseRole = Required(
                read,
                "DISPATCHER_RUNTIME_CONFIGURATION_DATABASE_ROLE"),
            EquipmentDatabaseRole = Optional(
                read,
                "DISPATCHER_RUNTIME_EQUIPMENT_DATABASE_ROLE"),
            StagingSecretKey = OptionalBase64Key(
                read,
                "DISPATCHER_RUNTIME_STAGING_SECRET_KEY"),
            DiagnosticLeaseDuration = OptionalPositiveMilliseconds(
                read,
                "DISPATCHER_RUNTIME_DIAGNOSTIC_LEASE_MS",
                TimeSpan.FromSeconds(30)),
            DiagnosticPollInterval = OptionalPositiveMilliseconds(
                read,
                "DISPATCHER_RUNTIME_DIAGNOSTIC_POLL_MS",
                TimeSpan.FromMilliseconds(250)),
            DeploymentLeaseDuration = PositiveMilliseconds(
                read,
                "DISPATCHER_RUNTIME_DEPLOYMENT_LEASE_MS"),
            ConfigurationReconciliationInterval = PositiveMilliseconds(
                read,
                "DISPATCHER_RUNTIME_CONFIGURATION_RECONCILIATION_MS"),
            ModbusLimits = new ModbusConfigurationLimits(
                OptionalPositiveInt(
                    read,
                    "DISPATCHER_RUNTIME_MODBUS_MAX_POINTS",
                    256),
                OptionalPositiveInt(
                    read,
                    "DISPATCHER_RUNTIME_MODBUS_MAX_REGISTERS_PER_POLL",
                    512)),
            SnmpLimits = new SnmpConfigurationLimits(
                OptionalPositiveInt(
                    read,
                    "DISPATCHER_RUNTIME_SNMP_MAX_POINTS",
                    256),
                OptionalPositiveInt(
                    read,
                    "DISPATCHER_RUNTIME_SNMP_MAX_OID_ARCS",
                    128),
                OptionalPositiveInt(
                    read,
                    "DISPATCHER_RUNTIME_SNMP_MAX_OID_BYTES",
                    512)),
            SnmpWireLimits = new SnmpWireLimits(
                OptionalPositiveInt(
                    read,
                    "DISPATCHER_RUNTIME_SNMP_MAX_COMMUNITY_BYTES",
                    128),
                OptionalPositiveInt(
                    read,
                    "DISPATCHER_RUNTIME_SNMP_MAX_REQUEST_BYTES",
                    4096)),
            ProtocolMaxResponseBytes = OptionalPositiveInt(
                read,
                "DISPATCHER_RUNTIME_PROTOCOL_MAX_RESPONSE_BYTES",
                65_536),
            ProtocolMaxObservations = OptionalPositiveInt(
                read,
                "DISPATCHER_RUNTIME_PROTOCOL_MAX_OBSERVATIONS",
                256),
            ProtocolMaxConcurrentOperations = OptionalPositiveInt(
                read,
                "DISPATCHER_RUNTIME_PROTOCOL_MAX_CONCURRENT_OPERATIONS",
                1),
        };
    }

    private static string Required(
        Func<string, string?> read,
        string name) =>
        read(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Required runtime setting {name} is absent.");

    private static string? Optional(
        Func<string, string?> read,
        string name) =>
        read(name) is { Length: > 0 } value ? value : null;

    private static byte[]? OptionalBase64Key(
        Func<string, string?> read,
        string name)
    {
        var raw = Optional(read, name);
        if (raw is null)
        {
            return null;
        }

        try
        {
            var key = Convert.FromBase64String(raw);
            return key.Length == 32
                ? key
                : throw new InvalidOperationException(
                    $"Runtime setting {name} must contain a base64-encoded 32-byte key.");
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                $"Runtime setting {name} must contain a base64-encoded 32-byte key.");
        }
    }

    private static int PositiveInt(
        Func<string, string?> read,
        string name) =>
        int.TryParse(
            Required(read, name),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value) &&
        value > 0
            ? value
            : throw new InvalidOperationException(
                $"Runtime setting {name} must be a positive integer.");

    private static int OptionalPositiveInt(
        Func<string, string?> read,
        string name,
        int defaultValue)
    {
        var raw = read(name);
        if (string.IsNullOrEmpty(raw))
        {
            return defaultValue;
        }

        return int.TryParse(
            raw,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value) &&
            value > 0
                ? value
                : throw new InvalidOperationException(
                    $"Runtime setting {name} must be a positive integer.");
    }

    private static ulong PositiveUlong(
        Func<string, string?> read,
        string name) =>
        ulong.TryParse(
            Required(read, name),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value) &&
        value > 0
            ? value
            : throw new InvalidOperationException(
                $"Runtime setting {name} must be a positive integer.");

    private static TimeSpan PositiveMilliseconds(
        Func<string, string?> read,
        string name)
    {
        if (!long.TryParse(
                Required(read, name),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value) ||
            value <= 0 ||
            value >
            TimeSpan.MaxValue.Ticks /
            TimeSpan.TicksPerMillisecond)
        {
            throw new InvalidOperationException(
                $"Runtime setting {name} must be a positive millisecond duration.");
        }

        return TimeSpan.FromTicks(
            checked(value * TimeSpan.TicksPerMillisecond));
    }

    private static TimeSpan OptionalPositiveMilliseconds(
        Func<string, string?> read,
        string name,
        TimeSpan defaultValue)
    {
        var raw = Optional(read, name);
        if (raw is null)
        {
            return defaultValue;
        }

        if (!long.TryParse(
                raw,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value) ||
            value <= 0 ||
            value > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond)
        {
            throw new InvalidOperationException(
                $"Runtime setting {name} must be a positive millisecond duration.");
        }

        return TimeSpan.FromTicks(checked(value * TimeSpan.TicksPerMillisecond));
    }
}

internal sealed class EnvironmentProtocolSecretResolver :
    IProtocolSecretResolver
{
    private const string Prefix = "env:";
    private readonly ProtocolWorkloadIdentity workloadIdentity;

    public EnvironmentProtocolSecretResolver(
        ProtocolWorkloadIdentity workloadIdentity) =>
        this.workloadIdentity = workloadIdentity;

    public ValueTask<ProtocolSecretLease> ResolveAsync(
        ProtocolSecretReference reference,
        ProtocolWorkloadIdentity requestingIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requestingIdentity != workloadIdentity ||
            !reference.Value.StartsWith(
                Prefix,
                StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "The runtime workload cannot resolve this secret reference.");
        }

        var variableName = reference.Value[Prefix.Length..];
        if (variableName.Length == 0 ||
            variableName.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character != '_'))
        {
            throw new UnauthorizedAccessException(
                "The secret reference is not an allowed environment reference.");
        }

        var rawSecret =
            Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrEmpty(rawSecret))
        {
            throw new InvalidOperationException(
                "The referenced runtime secret is unavailable.");
        }

        return ValueTask.FromResult(
            ProtocolSecretLease.Create(rawSecret));
    }
}
