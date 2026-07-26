using Dispatcher.RuntimeHost;
using Dispatcher.Semantics;
using Npgsql;

try
{
    var options = RuntimeHostOptions.FromEnvironment();
    var clock = SystemClock.Instance;
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    var application = new RuntimeHostApplication(
        cancellationToken => ValueTask.FromResult<IRuntimeHostSession>(
            ProductionRuntimeHostSession.Create(options, clock, clock)),
        options.ReconciliationInitialBackoff,
        options.ReconciliationMaxBackoff,
        IsTransientFailure,
        observer: snapshot =>
        {
            var reason = snapshot.ReasonCode is null
                ? string.Empty
                : $" reason={snapshot.ReasonCode}";
            Console.WriteLine(
                $"RuntimeHost state={snapshot.State} session={snapshot.SessionAttempt} retries={snapshot.RetryCount}{reason}");
        });

    var result = await application.RunAsync(shutdown.Token).ConfigureAwait(false);
    if (result.IsFailure)
    {
        Console.Error.WriteLine($"RuntimeHost stopped with error code {result.Error!.Code.Value}.");
        return 1;
    }

    return 0;
}
catch (Exception exception) when (
    exception is not OutOfMemoryException and not StackOverflowException)
{
    Console.Error.WriteLine(
        "Dispatcher Core/runtime host failed without disclosing configuration or secrets.");
    return 1;
}

static bool IsTransientFailure(Exception exception) =>
    exception is NpgsqlException or TimeoutException or IOException;
