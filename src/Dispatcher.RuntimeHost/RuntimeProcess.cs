using Dispatcher.Core;
using Dispatcher.Protocols;
using Dispatcher.Semantics;

namespace Dispatcher.RuntimeHost;

public sealed class RuntimeProcess
{
    private readonly CoreRuntimeHost core;
    private readonly ProtocolRuntimeSupervisor protocols;

    public RuntimeProcess(CoreRuntimeHost core, ProtocolRuntimeSupervisor protocols)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(protocols);
        this.core = core;
        this.protocols = protocols;
    }

    public Result RegisterProtocolSource(SourceId sourceId, ProtocolSourceController controller) =>
        protocols.Register(sourceId, controller);

    public Result ActivateBinding(SourceBinding binding) => core.ActivateBinding(binding);

    public async Task<Result<RuntimeIngressResult>> AcquireAsync(
        ProtocolSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        var acquired = await protocols.AcquireAsync(request, cancellationToken).ConfigureAwait(false);
        if (acquired.IsFailure)
        {
            return Result.Failure<RuntimeIngressResult>(acquired.Error!);
        }

        return await core.EnqueueAsync(acquired.Value, cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<ProtocolDiagnosticResult>> DiagnoseAsync(
        ProtocolDiagnosticRequest request,
        CancellationToken cancellationToken = default) =>
        protocols.DiagnoseAsync(request, cancellationToken);

    public async Task<Result> StartAsync(CancellationToken cancellationToken = default)
    {
        var coreStarted = await core.StartAsync(cancellationToken).ConfigureAwait(false);
        if (coreStarted.IsFailure)
        {
            return coreStarted;
        }

        var protocolsStarted = protocols.Start();
        if (protocolsStarted.IsFailure)
        {
            await core.DrainAsync(cancellationToken).ConfigureAwait(false);
        }

        return protocolsStarted;
    }

    public async Task<Result> StopAsync(CancellationToken cancellationToken = default)
    {
        var protocolsStopped = await protocols.StopAsync(cancellationToken).ConfigureAwait(false);
        if (protocolsStopped.IsFailure)
        {
            return protocolsStopped;
        }

        return await core.DrainAsync(cancellationToken).ConfigureAwait(false);
    }
}
