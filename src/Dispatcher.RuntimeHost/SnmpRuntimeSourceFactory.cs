using Dispatcher.Core;
using Dispatcher.ProtocolCommissioning;
using Dispatcher.Protocols;
using Dispatcher.Semantics;
using Dispatcher.Snmp;

namespace Dispatcher.RuntimeHost;

public sealed class SnmpRuntimeSourceSet : IDisposable
{
    private int disposed;

    internal SnmpRuntimeSourceSet(
        IReadOnlyList<SnmpRuntimeSource> sources) =>
        Sources = sources;

    internal IReadOnlyList<SnmpRuntimeSource> Sources { get; }

    internal IReadOnlyDictionary<SourceId, ProtocolSourceController> Controllers =>
        Sources.ToDictionary(
            source => source.Binding.SourceId,
            source => source.Source.Controller);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        foreach (var source in Sources)
        {
            source.Source.Dispose();
        }
    }
}

internal sealed record SnmpRuntimeSource(
    SnmpV2cSourceConfiguration Configuration,
    SourceBinding Binding,
    SnmpV2cSource Source) : IRuntimeProtocolSource
{
    public ProtocolSourceController Controller => Source.Controller;

    public ProtocolSecretReference? SecretReference =>
        Configuration.CommunityReference;

    public Result<RuntimeCut> CreateUnavailableCut(ulong scheduleSequence) =>
        Source.CreateUnavailableCut(Binding, scheduleSequence);
}

public static class SnmpRuntimeSourceFactory
{
    public static Result<SnmpRuntimeSourceSet> Create(
        ProtocolActivationPlan? plan,
        IReadOnlyList<SourceBinding> bindings,
        SnmpConfigurationLimits configurationLimits,
        SnmpWireLimits wireLimits,
        ProtocolWorkloadIdentity workloadIdentity,
        ProtocolIoLimits ioLimits,
        IProtocolSecretResolver secretResolver,
        ISnmpDatagramClientFactory clientFactory,
        IWallClock wallClock)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(configurationLimits);
        ArgumentNullException.ThrowIfNull(wireLimits);
        ArgumentNullException.ThrowIfNull(ioLimits);
        ArgumentNullException.ThrowIfNull(secretResolver);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(wallClock);
        if (plan is null)
        {
            return bindings.Count == 0
                ? Result.Success(new SnmpRuntimeSourceSet([]))
                : Failure(
                    "runtime.snmp_binding_plan",
                    "SNMP bindings exist without a protocol activation plan.");
        }

        var bindingBySource = bindings.ToDictionary(binding => binding.SourceId);
        if (bindingBySource.Count != bindings.Count ||
            bindingBySource.Count != plan.SnmpSources.Count ||
            plan.SnmpSources.Any(source => !bindingBySource.ContainsKey(source.SourceId)))
        {
            return Failure(
                "runtime.snmp_binding_plan",
                "SNMP source bindings do not match the activation plan.");
        }

        var created = new List<SnmpRuntimeSource>(plan.SnmpSources.Count);
        foreach (var configuration in plan.SnmpSources)
        {
            var source = SnmpV2cSource.Create(
                configuration,
                configurationLimits,
                wireLimits,
                workloadIdentity,
                ioLimits,
                secretResolver,
                clientFactory,
                wallClock);
            if (source.IsFailure)
            {
                foreach (var item in created)
                {
                    item.Source.Dispose();
                }

                return Result.Failure<SnmpRuntimeSourceSet>(source.Error!);
            }

            created.Add(new SnmpRuntimeSource(
                configuration,
                bindingBySource[configuration.SourceId],
                source.Value));
        }

        return Result.Success(new SnmpRuntimeSourceSet(created));
    }

    private static Result<SnmpRuntimeSourceSet> Failure(
        string code,
        string message) =>
        Result.Failure<SnmpRuntimeSourceSet>(
            new OperationError(ErrorCode.From(code), message));
}
