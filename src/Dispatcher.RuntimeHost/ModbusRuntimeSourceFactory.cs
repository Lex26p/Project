using Dispatcher.Core;
using Dispatcher.Modbus;
using Dispatcher.ProtocolCommissioning;
using Dispatcher.Protocols;
using Dispatcher.Semantics;

namespace Dispatcher.RuntimeHost;

public sealed class ModbusRuntimeSourceSet : IDisposable
{
    private int disposed;

    internal ModbusRuntimeSourceSet(
        IReadOnlyList<ModbusRuntimeSource> sources) =>
        Sources = sources;

    internal IReadOnlyList<ModbusRuntimeSource> Sources { get; }

    public IReadOnlyList<SourceBinding> Bindings =>
        Sources.Select(source => source.Binding).ToArray();

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

internal sealed record ModbusRuntimeSource(
    ModbusTcpSourceConfiguration Configuration,
    SourceBinding Binding,
    ModbusTcpSource Source);

public static class ModbusRuntimeSourceFactory
{
    public static Result<ModbusRuntimeSourceSet> Create(
        ProtocolActivationPlan? plan,
        IReadOnlyList<SourceBinding> bindings,
        ModbusConfigurationLimits configurationLimits,
        ProtocolWorkloadIdentity workloadIdentity,
        ProtocolIoLimits ioLimits,
        IModbusTcpConnectionFactory connectionFactory,
        IWallClock wallClock)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(configurationLimits);
        ArgumentNullException.ThrowIfNull(ioLimits);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(wallClock);
        if (plan is null)
        {
            return bindings.Count == 0
                ? Result.Success(new ModbusRuntimeSourceSet([]))
                : Failure(
                    "runtime.modbus_binding_plan",
                    "Modbus bindings exist without a protocol activation plan.");
        }

        var bindingBySource = bindings.ToDictionary(binding => binding.SourceId);
        if (bindingBySource.Count != bindings.Count ||
            bindingBySource.Count != plan.ModbusSources.Count ||
            plan.ModbusSources.Any(source => !bindingBySource.ContainsKey(source.SourceId)))
        {
            return Failure(
                "runtime.modbus_binding_plan",
                "Modbus source bindings do not match the activation plan.");
        }

        var created = new List<ModbusRuntimeSource>(plan.ModbusSources.Count);
        foreach (var configuration in plan.ModbusSources)
        {
            var source = ModbusTcpSource.Create(
                configuration,
                configurationLimits,
                workloadIdentity,
                ioLimits,
                connectionFactory,
                wallClock);
            if (source.IsFailure)
            {
                foreach (var item in created)
                {
                    item.Source.Dispose();
                }

                return Result.Failure<ModbusRuntimeSourceSet>(source.Error!);
            }

            created.Add(new ModbusRuntimeSource(
                configuration,
                bindingBySource[configuration.SourceId],
                source.Value));
        }

        return Result.Success(new ModbusRuntimeSourceSet(created));
    }

    private static Result<ModbusRuntimeSourceSet> Failure(
        string code,
        string message) =>
        Result.Failure<ModbusRuntimeSourceSet>(
            new OperationError(ErrorCode.From(code), message));
}
