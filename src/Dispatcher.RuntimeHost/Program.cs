using Dispatcher.Core;
using Dispatcher.Protocols;
using Dispatcher.RuntimeHost;
using Dispatcher.Semantics;
using Npgsql;

try
{
    var options = RuntimeHostOptions.FromEnvironment();
    var clock = SystemClock.Instance;
    await using var dataSource = NpgsqlDataSource.Create(options.ConnectionString);
    using var core = new CoreRuntimeHost(
        options.ScopeId,
        new CoreRuntime(
            options.ScopeId,
            clock,
            clock,
            new RuntimeCurrentLimits(options.MaxCurrentPoints, options.RetainedCurrentChanges)),
        new CoreRuntimeStore(dataSource, options.DatabaseRole, clock),
        new RuntimeIngressLimits(options.IngressCapacity));
    var protocols = new ProtocolRuntimeSupervisor(options.WorkloadIdentity, options.MaxProtocolSources);
    _ = new EnvironmentProtocolSecretResolver(options.WorkloadIdentity);
    var process = new RuntimeProcess(core, protocols);
    var started = await process.StartAsync().ConfigureAwait(false);
    if (started.IsFailure)
    {
        Console.Error.WriteLine($"Runtime host startup rejected: {started.Error!.Code.Value}");
        return 1;
    }

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };
    Console.WriteLine("Dispatcher Core/runtime host is running.");
    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
    }

    var stopped = await process.StopAsync().ConfigureAwait(false);
    if (stopped.IsFailure)
    {
        Console.Error.WriteLine($"Runtime host shutdown rejected: {stopped.Error!.Code.Value}");
        return 1;
    }

    return 0;
}
catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
{
    Console.Error.WriteLine("Dispatcher Core/runtime host failed without disclosing configuration or secrets.");
    return 1;
}
