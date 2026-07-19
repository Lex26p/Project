using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class PostgreSqlTestGroup : ICollectionFixture<PostgreSqlClusterFixture>
{
    public const string Name = "PostgreSQL";
}

public sealed class PostgreSqlClusterFixture : IAsyncLifetime
{
    public const string OwnerARole = "dispatcher_test_owner_a";
    public const string OwnerBRole = "dispatcher_test_owner_b";

    private string? clusterRoot;
    private string? dataDirectory;
    private string? postgresBin;
    private int port;

    public async Task InitializeAsync()
    {
        postgresBin = FindPostgreSqlBin();
        clusterRoot = Path.Combine(Path.GetTempPath(), $"dispatcher-pg-{Guid.NewGuid():N}");
        dataDirectory = Path.Combine(clusterRoot, "data");
        Directory.CreateDirectory(clusterRoot);
        port = ReserveTcpPort();

        await RunAsync(
            Path.Combine(postgresBin, "initdb.exe"),
            [
                "-D", dataDirectory,
                "--username=postgres",
                "--auth-local=trust",
                "--auth-host=trust",
                "--encoding=UTF8",
                "--locale=C",
            ]).ConfigureAwait(false);

        await RunAsync(
            Path.Combine(postgresBin, "pg_ctl.exe"),
            [
                "-D", dataDirectory,
                "-l", Path.Combine(clusterRoot, "postgres.log"),
                "-o", $"-h 127.0.0.1 -p {port.ToString(CultureInfo.InvariantCulture)}",
                "-w", "start",
            ],
            captureOutput: false).ConfigureAwait(false);

        await using var connection = new NpgsqlConnection(BuildConnectionString("postgres"));
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"CREATE ROLE {QuoteIdentifier(OwnerARole)} NOLOGIN; CREATE ROLE {QuoteIdentifier(OwnerBRole)} NOLOGIN;",
            connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var databaseName = $"dispatcher_test_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(BuildConnectionString("postgres"));
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"CREATE DATABASE {QuoteIdentifier(databaseName)};", connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);

        return new TestDatabase(databaseName, BuildConnectionString(databaseName), DropDatabaseAsync);
    }

    public async Task<string> DumpDatabaseAsync(TestDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var dumpPath = Path.Combine(RequiredClusterRoot, $"{database.Name}.dump");
        await RunAsync(
            Path.Combine(RequiredPostgresBin, "pg_dump.exe"),
            [
                "--host=127.0.0.1",
                $"--port={port.ToString(CultureInfo.InvariantCulture)}",
                "--username=postgres",
                "--format=custom",
                $"--file={dumpPath}",
                database.Name,
            ]).ConfigureAwait(false);
        return dumpPath;
    }

    public Task RestoreDatabaseAsync(TestDatabase database, string dumpPath)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpPath);
        return RunAsync(
            Path.Combine(RequiredPostgresBin, "pg_restore.exe"),
            [
                "--host=127.0.0.1",
                $"--port={port.ToString(CultureInfo.InvariantCulture)}",
                "--username=postgres",
                $"--dbname={database.Name}",
                "--exit-on-error",
                dumpPath,
            ]);
    }

    public async Task DisposeAsync()
    {
        if (postgresBin is not null && dataDirectory is not null)
        {
            await RunAsync(
                Path.Combine(postgresBin, "pg_ctl.exe"),
                ["-D", dataDirectory, "-m", "fast", "-w", "stop"]).ConfigureAwait(false);
        }

        if (clusterRoot is not null)
        {
            DeleteTemporaryCluster(clusterRoot);
        }
    }

    private async ValueTask DropDatabaseAsync(string databaseName)
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(BuildConnectionString("postgres"));
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS {QuoteIdentifier(databaseName)} WITH (FORCE);",
            connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private string BuildConnectionString(string databaseName) => new NpgsqlConnectionStringBuilder
    {
        Host = IPAddress.Loopback.ToString(),
        Port = port,
        Database = databaseName,
        Username = "postgres",
        Pooling = false,
        Timeout = 5,
        CommandTimeout = 15,
    }.ConnectionString;

    private string RequiredClusterRoot => clusterRoot ??
        throw new InvalidOperationException("The PostgreSQL test cluster has not been initialized.");

    private string RequiredPostgresBin => postgresBin ??
        throw new InvalidOperationException("PostgreSQL binaries have not been discovered.");

    private static string FindPostgreSqlBin()
    {
        var configured = Environment.GetEnvironmentVariable("DISPATCHER_POSTGRES_BIN");
        if (ContainsPostgreSqlBinaries(configured))
        {
            return Path.GetFullPath(configured!);
        }

        var installationsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PostgreSQL");
        if (Directory.Exists(installationsRoot))
        {
            var discovered = Directory.GetDirectories(installationsRoot)
                .Select(directory => Path.Combine(directory, "bin"))
                .Where(ContainsPostgreSqlBinaries)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (discovered is not null)
            {
                return discovered;
            }
        }

        throw new InvalidOperationException(
            "PostgreSQL binaries were not found. Set DISPATCHER_POSTGRES_BIN to a PostgreSQL bin directory.");
    }

    private static bool ContainsPostgreSqlBinaries(string? directory) =>
        !string.IsNullOrWhiteSpace(directory) &&
        File.Exists(Path.Combine(directory, "initdb.exe")) &&
        File.Exists(Path.Combine(directory, "pg_ctl.exe")) &&
        File.Exists(Path.Combine(directory, "pg_dump.exe")) &&
        File.Exists(Path.Combine(directory, "pg_restore.exe"));

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task RunAsync(
        string executable,
        IEnumerable<string> arguments,
        bool captureOutput = true)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            RedirectStandardError = captureOutput,
            RedirectStandardOutput = captureOutput,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{Path.GetFileName(executable)}'.");
        }

        var outputTask = captureOutput ? process.StandardOutput.ReadToEndAsync() : null;
        var errorTask = captureOutput ? process.StandardError.ReadToEndAsync() : null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var output = outputTask is null ? string.Empty : await outputTask.ConfigureAwait(false);
        var error = errorTask is null ? string.Empty : await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(executable)}' failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{error}");
        }
    }

    private static string QuoteIdentifier(string value) => $"\"{value}\"";

    private static void DeleteTemporaryCluster(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith("dispatcher-pg-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete a directory outside the Dispatcher test prefix.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }
}

public sealed class TestDatabase : IAsyncDisposable
{
    private readonly Func<string, ValueTask> dropDatabase;

    internal TestDatabase(string name, string connectionString, Func<string, ValueTask> dropDatabase)
    {
        Name = name;
        ConnectionString = connectionString;
        this.dropDatabase = dropDatabase;
    }

    public string Name { get; }

    public string ConnectionString { get; }

    public ValueTask DisposeAsync() => dropDatabase(Name);
}
