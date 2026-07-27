using System.Collections.Concurrent;
using System.Diagnostics;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace Dispatcher.IntegrationTests.ProcessTests;

internal sealed class ManagedDispatcherProcess : IAsyncDisposable
{
    private const int MaximumCapturedLines = 500;
    private const int MaximumLineLength = 2_000;
    private readonly DiagnosticsProcess process;
    private readonly string name;
    private readonly string[] secrets;
    private readonly ConcurrentQueue<string> output = new();
    private readonly SemaphoreSlim outputChanged = new(0);
    private readonly Task standardOutput;
    private readonly Task standardError;

    private ManagedDispatcherProcess(
        DiagnosticsProcess process,
        string name,
        IEnumerable<string> secrets)
    {
        this.process = process;
        this.name = name;
        this.secrets = secrets
            .Where(static value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static value => value.Length)
            .ToArray();
        standardOutput = CaptureAsync(process.StandardOutput, "stdout");
        standardError = CaptureAsync(process.StandardError, "stderr");
    }

    public bool HasExited => process.HasExited;

    public int Id => process.Id;

    public static ManagedDispatcherProcess StartDotNet(
        string name,
        string assemblyPath,
        IReadOnlyDictionary<string, string> environment,
        IEnumerable<string> secrets,
        IEnumerable<string>? arguments = null,
        params string[] clearEnvironmentPrefixes)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
        };
        startInfo.ArgumentList.Add(assemblyPath);
        if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        foreach (var key in startInfo.Environment.Keys.ToArray())
        {
            if (clearEnvironmentPrefixes.Any(prefix =>
                    key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                startInfo.Environment.Remove(key);
            }
        }
        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = new DiagnosticsProcess { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start {name}.");
        }

        return new ManagedDispatcherProcess(process, name, secrets);
    }

    public static ManagedDispatcherProcess StartDotNetCommand(
        string name,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        IEnumerable<string> secrets,
        params string[] clearEnvironmentPrefixes)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var key in startInfo.Environment.Keys.ToArray())
        {
            if (clearEnvironmentPrefixes.Any(prefix =>
                    key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                startInfo.Environment.Remove(key);
            }
        }
        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = new DiagnosticsProcess { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start {name}.");
        }
        return new ManagedDispatcherProcess(process, name, secrets);
    }

    public bool OutputContains(string value) =>
        output.Any(line => line.Contains(value, StringComparison.Ordinal));

    public async Task WaitForOutputAsync(
        string value,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        while (!OutputContains(value))
        {
            ThrowIfExited();
            try
            {
                await outputChanged.WaitAsync(bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"{name} did not report '{value}' within {timeout}.{Environment.NewLine}{Diagnostics()}");
            }
        }
    }

    public async Task ShutdownAsync(TimeSpan timeout)
    {
        if (process.HasExited)
        {
            AssertSuccessfulExit();
            return;
        }

        await process.StandardInput.WriteLineAsync("shutdown").ConfigureAwait(false);
        await process.StandardInput.FlushAsync().ConfigureAwait(false);
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            process.Kill();
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw new TimeoutException(
                $"{name} did not stop gracefully within {timeout}.{Environment.NewLine}{Diagnostics()}");
        }
        AssertSuccessfulExit();
    }

    public async Task WaitForSuccessfulExitAsync(TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"{name} did not exit within {timeout}.{Environment.NewLine}{Diagnostics()}");
        }
        AssertSuccessfulExit();
    }

    public async Task KillAsync()
    {
        if (!process.HasExited)
        {
            process.Kill();
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    public string Diagnostics()
    {
        var lines = output.ToArray();
        return $"{name} pid={process.Id} exited={process.HasExited}" +
               (process.HasExited ? $" code={process.ExitCode}" : string.Empty) +
               Environment.NewLine +
               string.Join(Environment.NewLine, lines);
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            process.Kill();
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        process.StandardOutput.Dispose();
        process.StandardError.Dispose();
        try
        {
            await Task.WhenAll(standardOutput, standardError)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        process.Dispose();
        outputChanged.Dispose();
    }

    private async Task CaptureAsync(StreamReader reader, string channel)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var redacted = Redact(line);
                if (redacted.Length > MaximumLineLength)
                {
                    redacted = redacted[..MaximumLineLength] + "…";
                }
                output.Enqueue($"[{channel}] {redacted}");
                while (output.Count > MaximumCapturedLines)
                {
                    output.TryDequeue(out _);
                }
                outputChanged.Release();
            }
        }
        catch (Exception exception) when (
            exception is ObjectDisposedException or IOException)
        {
        }
    }

    private string Redact(string value)
    {
        foreach (var secret in secrets)
        {
            value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }
        return value;
    }

    private void ThrowIfExited()
    {
        if (process.HasExited)
        {
            throw new InvalidOperationException(
                $"{name} exited unexpectedly.{Environment.NewLine}{Diagnostics()}");
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
