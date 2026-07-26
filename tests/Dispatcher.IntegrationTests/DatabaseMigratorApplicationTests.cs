using System.Globalization;
using Dispatcher.DatabaseMigrator;
using Xunit;

namespace Dispatcher.IntegrationTests;

public sealed class DatabaseMigratorApplicationTests
{
    [Fact]
    public async Task PreCanceledExecutionReturnsCancellationExitCodeWithoutConfigurationAccess()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        int exitCode = await DatabaseMigratorApplication.RunAsync(
            [],
            output,
            error,
            cancellationSource.Token);

        Assert.Equal(130, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal($"Database migration was canceled.{Environment.NewLine}", error.ToString());
    }

    [Fact]
    public async Task HelpReturnsSuccessWithoutConfigurationAccess()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        int exitCode = await DatabaseMigratorApplication.RunAsync(
            ["--help"],
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Dispatcher.DatabaseMigrator", output.ToString());
        Assert.Contains(MigrationEnvironmentVariables.ConnectionString, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }
}
