using Dispatcher.Core;
using Dispatcher.Protocols;

namespace Dispatcher.RuntimeHost;

internal sealed record RuntimeHostOptions(
    RuntimeScopeId ScopeId,
    ProtocolWorkloadIdentity WorkloadIdentity,
    string ConnectionString,
    string DatabaseRole,
    int MaxCurrentPoints,
    int RetainedCurrentChanges,
    int IngressCapacity,
    int MaxProtocolSources)
{
    public static RuntimeHostOptions FromEnvironment() => new(
        RuntimeScopeId.From(Guid.Parse(Required("DISPATCHER_RUNTIME_SCOPE_ID"))),
        ProtocolWorkloadIdentity.From(Required("DISPATCHER_RUNTIME_WORKLOAD_IDENTITY")),
        Required("DISPATCHER_RUNTIME_CONNECTION_STRING"),
        Required("DISPATCHER_RUNTIME_DATABASE_ROLE"),
        PositiveInt("DISPATCHER_RUNTIME_MAX_CURRENT_POINTS"),
        PositiveInt("DISPATCHER_RUNTIME_RETAINED_CURRENT_CHANGES"),
        PositiveInt("DISPATCHER_RUNTIME_INGRESS_CAPACITY"),
        PositiveInt("DISPATCHER_RUNTIME_MAX_PROTOCOL_SOURCES"));

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required runtime setting {name} is absent.");

    private static int PositiveInt(string name) =>
        int.TryParse(Required(name), out var value) && value > 0
            ? value
            : throw new InvalidOperationException($"Runtime setting {name} must be a positive integer.");
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
