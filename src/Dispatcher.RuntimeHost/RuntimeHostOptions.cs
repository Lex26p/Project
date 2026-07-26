using System.Globalization;
using Dispatcher.Core;
using Dispatcher.Protocols;

namespace Dispatcher.RuntimeHost;

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
    public PollScheduleLimits CreatePollScheduleLimits() =>
        new(PollTimeout, SchedulerMaxBindings, SchedulerMaxInFlight);

    public static RuntimeHostOptions FromEnvironment() =>
        FromSettings(Environment.GetEnvironmentVariable);

    public static RuntimeHostOptions FromSettings(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var schedulerMaxBindings = PositiveInt(read, "DISPATCHER_RUNTIME_SCHEDULER_MAX_BINDINGS");
        var schedulerMaxInFlight = PositiveInt(read, "DISPATCHER_RUNTIME_SCHEDULER_MAX_IN_FLIGHT");
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

        return new RuntimeHostOptions(
            RuntimeScopeId.From(Guid.Parse(Required(read, "DISPATCHER_RUNTIME_SCOPE_ID"))),
            ProtocolWorkloadIdentity.From(Required(read, "DISPATCHER_RUNTIME_WORKLOAD_IDENTITY")),
            Required(read, "DISPATCHER_RUNTIME_CONNECTION_STRING"),
            Required(read, "DISPATCHER_RUNTIME_DATABASE_ROLE"),
            Required(read, "DISPATCHER_RUNTIME_SIMULATOR_DATABASE_ROLE"),
            PositiveInt(read, "DISPATCHER_RUNTIME_MAX_CURRENT_POINTS"),
            PositiveInt(read, "DISPATCHER_RUNTIME_RETAINED_CURRENT_CHANGES"),
            PositiveInt(read, "DISPATCHER_RUNTIME_INGRESS_CAPACITY"),
            PositiveInt(read, "DISPATCHER_RUNTIME_MAX_PROTOCOL_SOURCES"),
            PositiveMilliseconds(read, "DISPATCHER_RUNTIME_POLL_INTERVAL_MS"),
            PositiveMilliseconds(read, "DISPATCHER_RUNTIME_POLL_TIMEOUT_MS"),
            schedulerMaxBindings,
            schedulerMaxInFlight,
            reconciliationInitialBackoff,
            reconciliationMaxBackoff);
    }

    private static string Required(Func<string, string?> read, string name) =>
        read(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required runtime setting {name} is absent.");

    private static int PositiveInt(Func<string, string?> read, string name) =>
        int.TryParse(
            Required(read, name),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value) && value > 0
                ? value
                : throw new InvalidOperationException($"Runtime setting {name} must be a positive integer.");

    private static TimeSpan PositiveMilliseconds(Func<string, string?> read, string name)
    {
        if (!long.TryParse(
                Required(read, name),
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

internal sealed class EnvironmentProtocolSecretResolver : IProtocolSecretResolver
{
    private const string Prefix = "env:";
    private readonly ProtocolWorkloadIdentity workloadIdentity;

    public EnvironmentProtocolSecretResolver(ProtocolWorkloadIdentity workloadIdentity) =>
        this.workloadIdentity = workloadIdentity;

    public ValueTask<ProtocolSecretLease> ResolveAsync(
        ProtocolSecretReference reference,
        ProtocolWorkloadIdentity requestingIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requestingIdentity != workloadIdentity || !reference.Value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The runtime workload cannot resolve this secret reference.");
        }

        var variableName = reference.Value[Prefix.Length..];
        if (variableName.Length == 0 || variableName.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            throw new UnauthorizedAccessException("The secret reference is not an allowed environment reference.");
        }

        var rawSecret = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrEmpty(rawSecret))
        {
            throw new InvalidOperationException("The referenced runtime secret is unavailable.");
        }

        return ValueTask.FromResult(ProtocolSecretLease.Create(rawSecret));
    }
}
