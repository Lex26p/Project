using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using Xunit;

namespace Dispatcher.BrowserTests;

public sealed class BrowserServerFixture :
    IAsyncLifetime
{
    private static readonly TimeSpan ProcessTimeout =
        TimeSpan.FromSeconds(180);
    private static readonly TimeSpan StartupTimeout =
        TimeSpan.FromSeconds(30);
    private IPlaywright? playwright;
    private IBrowser? browser;
    private CapturedProcess? server;
    private string? publishDirectory;

    public Uri Address { get; private set; } =
        new("http://127.0.0.1/");

    public async Task InitializeAsync()
    {
        try
        {
            var projectRoot = FindProjectRoot();
            publishDirectory = Path.Combine(
                Path.GetTempPath(),
                $"dispatcher-c08-browser-{Guid.NewGuid():N}");
            Directory.CreateDirectory(
                publishDirectory);

            await PublishServerAsync(
                    projectRoot,
                    publishDirectory)
                .ConfigureAwait(false);

            var port = ReserveTcpPort();
            Address = new Uri(
                $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");
            server = CapturedProcess.Start(
                "Dispatcher.Server",
                "dotnet",
                [
                    Path.Combine(
                        publishDirectory,
                        "Dispatcher.Server.dll"),
                ],
                publishDirectory,
                new Dictionary<string, string>
                {
                    ["ASPNETCORE_ENVIRONMENT"] =
                        "Production",
                    ["ASPNETCORE_URLS"] =
                        Address.ToString()
                            .TrimEnd('/'),
                    ["ASPNETCORE_CONTENTROOT"] =
                        publishDirectory,
                    ["DISPATCHER_PROCESS_CONTROL_STDIN"] =
                        "1",
                });
            await WaitForServerAsync(
                    server,
                    Address,
                    StartupTimeout)
                .ConfigureAwait(false);

            playwright =
                await Playwright.CreateAsync()
                    .ConfigureAwait(false);
            try
            {
                browser =
                    await playwright.Chromium
                        .LaunchAsync(
                            new BrowserTypeLaunchOptions
                            {
                                Headless =
                                    !string.Equals(
                                        Environment
                                            .GetEnvironmentVariable(
                                                "DISPATCHER_BROWSER_HEADED"),
                                        "1",
                                        StringComparison.Ordinal),
                            })
                        .ConfigureAwait(false);
            }
            catch (PlaywrightException exception)
            {
                throw new InvalidOperationException(
                    "Playwright Chromium is not installed. Build the browser test project and run its generated playwright.ps1 script with 'install chromium'.",
                    exception);
            }
        }
        catch
        {
            await DisposeAsync()
                .ConfigureAwait(false);
            throw;
        }
    }

    public Task<BrowserScenario> CreateScenarioAsync() =>
        BrowserScenario.CreateAsync(
            RequiredBrowser,
            Address);

    public async Task DisposeAsync()
    {
        if (browser is not null)
        {
            await browser.CloseAsync()
                .ConfigureAwait(false);
            browser = null;
        }

        playwright?.Dispose();
        playwright = null;

        if (server is not null)
        {
            await server.StopAsync(
                    TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            await server.DisposeAsync()
                .ConfigureAwait(false);
            server = null;
        }

        if (publishDirectory is not null)
        {
            DeletePublishDirectory(
                publishDirectory);
            publishDirectory = null;
        }
    }

    private IBrowser RequiredBrowser =>
        browser ??
        throw new InvalidOperationException(
            "Browser fixture is not initialized.");

    private static async Task PublishServerAsync(
        string projectRoot,
        string outputDirectory)
    {
        var project = Path.Combine(
            projectRoot,
            "src",
            "Dispatcher.Server",
            "Dispatcher.Server.csproj");
        await CapturedProcess.RunAsync(
                "Dispatcher.Server publish",
                "dotnet",
                [
                    "publish",
                    project,
                    "--configuration",
                    BuildConfiguration(),
                    "--no-build",
                    "--no-restore",
                    "--output",
                    outputDirectory,
                ],
                projectRoot,
                new Dictionary<string, string>(),
                ProcessTimeout)
            .ConfigureAwait(false);
    }

    private static async Task WaitForServerAsync(
        CapturedProcess process,
        Uri address,
        TimeSpan timeout)
    {
        using var http =
            new HttpClient
            {
                BaseAddress = address,
                Timeout =
                    TimeSpan.FromSeconds(2),
            };
        using var bounded =
            new CancellationTokenSource(timeout);
        while (true)
        {
            process.ThrowIfExited();
            try
            {
                using var response =
                    await http.GetAsync(
                            string.Empty,
                            bounded.Token)
                        .ConfigureAwait(false);
                if (response.StatusCode ==
                    HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
                when (!bounded.IsCancellationRequested)
            {
            }

            try
            {
                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        bounded.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Dispatcher.Server did not start within {timeout}.{Environment.NewLine}{process.Diagnostics()}");
            }
        }
    }

    private static int ReserveTcpPort()
    {
        using var listener =
            new TcpListener(
                IPAddress.Loopback,
                0);
        listener.Start();
        return ((IPEndPoint)
                listener.LocalEndpoint)
            .Port;
    }

    private static string FindProjectRoot()
    {
        for (var current =
                 new DirectoryInfo(
                     AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(
                    Path.Combine(
                        current.FullName,
                        "Dispatcher.slnx")))
            {
                return current.FullName;
            }
        }

        throw new InvalidOperationException(
            "Dispatcher project root was not found.");
    }

    private static string BuildConfiguration()
    {
        for (var current =
                 new DirectoryInfo(
                     AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (string.Equals(
                    current.Name,
                    "Release",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Release";
            }

            if (string.Equals(
                    current.Name,
                    "Debug",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Debug";
            }
        }

        return "Release";
    }

    private static void DeletePublishDirectory(
        string path)
    {
        var fullPath =
            Path.GetFullPath(path);
        var tempRoot =
            Path.GetFullPath(
                Path.GetTempPath());
        if (!fullPath.StartsWith(
                tempRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath)
                .StartsWith(
                    "dispatcher-c08-browser-",
                    StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Refusing to delete a directory outside the Dispatcher browser-test prefix.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(
                fullPath,
                recursive: true);
        }
    }

    private sealed class CapturedProcess :
        IAsyncDisposable
    {
        private const int MaximumCapturedLines =
            400;
        private readonly Process process;
        private readonly string name;
        private readonly ConcurrentQueue<string>
            output = new();
        private readonly Task standardOutput;
        private readonly Task standardError;

        private CapturedProcess(
            Process process,
            string name)
        {
            this.process = process;
            this.name = name;
            standardOutput =
                CaptureAsync(
                    process.StandardOutput,
                    "stdout");
            standardError =
                CaptureAsync(
                    process.StandardError,
                    "stderr");
        }

        public static CapturedProcess Start(
            string name,
            string executable,
            string[] arguments,
            string workingDirectory,
            Dictionary<string, string>
                environment)
        {
            var startInfo =
                CreateStartInfo(
                    executable,
                    arguments,
                    workingDirectory,
                    environment);
            var process =
                new Process
                {
                    StartInfo = startInfo,
                };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException(
                    $"Failed to start {name}.");
            }

            return new CapturedProcess(
                process,
                name);
        }

        public static async Task RunAsync(
            string name,
            string executable,
            string[] arguments,
            string workingDirectory,
            Dictionary<string, string>
                environment,
            TimeSpan timeout)
        {
            await using var process =
                Start(
                    name,
                    executable,
                    arguments,
                    workingDirectory,
                    environment);
            try
            {
                await process.process
                    .WaitForExitAsync()
                    .WaitAsync(timeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await process.KillAsync()
                    .ConfigureAwait(false);
                throw new TimeoutException(
                    $"{name} did not exit within {timeout}.{Environment.NewLine}{process.Diagnostics()}");
            }

            process.AssertSuccessfulExit();
        }

        public void ThrowIfExited()
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"{name} exited unexpectedly.{Environment.NewLine}{Diagnostics()}");
            }
        }

        public async Task StopAsync(
            TimeSpan timeout)
        {
            if (process.HasExited)
            {
                AssertSuccessfulExit();
                return;
            }

            await process.StandardInput
                .WriteLineAsync("shutdown")
                .ConfigureAwait(false);
            await process.StandardInput
                .FlushAsync()
                .ConfigureAwait(false);
            try
            {
                await process.WaitForExitAsync()
                    .WaitAsync(timeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await KillAsync()
                    .ConfigureAwait(false);
            }
        }

        public string Diagnostics() =>
            $"{name} pid={process.Id} exited={process.HasExited}" +
            (process.HasExited
                ? $" code={process.ExitCode}"
                : string.Empty) +
            Environment.NewLine +
            string.Join(
                Environment.NewLine,
                output);

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                await KillAsync()
                    .ConfigureAwait(false);
            }

            process.StandardInput.Dispose();
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            try
            {
                await Task.WhenAll(
                        standardOutput,
                        standardError)
                    .WaitAsync(
                        TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }

            process.Dispose();
        }

        private static ProcessStartInfo
            CreateStartInfo(
                string executable,
                string[] arguments,
                string workingDirectory,
                Dictionary<
                    string,
                    string> environment)
        {
            var startInfo =
                new ProcessStartInfo(executable)
                {
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    WorkingDirectory =
                        workingDirectory,
                };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(
                    argument);
            }

            foreach (var pair in environment)
            {
                startInfo.Environment[
                    pair.Key] = pair.Value;
            }

            return startInfo;
        }

        private async Task CaptureAsync(
            StreamReader reader,
            string channel)
        {
            try
            {
                while (await reader
                           .ReadLineAsync()
                           .ConfigureAwait(false)
                       is { } line)
                {
                    output.Enqueue(
                        $"[{channel}] {line}");
                    while (output.Count >
                           MaximumCapturedLines)
                    {
                        output.TryDequeue(
                            out _);
                    }
                }
            }
            catch (Exception exception)
                when (exception is
                    ObjectDisposedException or
                    IOException)
            {
            }
        }

        private async Task KillAsync()
        {
            if (!process.HasExited)
            {
                process.Kill(
                    entireProcessTree: true);
                await process.WaitForExitAsync()
                    .ConfigureAwait(false);
            }
        }

        private void AssertSuccessfulExit()
        {
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{name} exited with code {process.ExitCode}.{Environment.NewLine}{Diagnostics()}");
            }
        }
    }
}
