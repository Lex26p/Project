using Dispatcher.DatabaseMigrator;

using var cancellationSource = new CancellationTokenSource();

ConsoleCancelEventHandler cancellationHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};

Console.CancelKeyPress += cancellationHandler;

try
{
    return await DatabaseMigratorApplication.RunAsync(
        args,
        Console.Out,
        Console.Error,
        cancellationSource.Token).ConfigureAwait(false);
}
finally
{
    Console.CancelKeyPress -= cancellationHandler;
}
