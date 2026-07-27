using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Dispatcher.Alarm;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.DatabaseMigrator;
using Dispatcher.Facilities;
using Dispatcher.Identity;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Dispatcher.Server;
using Dispatcher.Simulator;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace Dispatcher.IntegrationTests.ProcessTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class SimulatorProcessE2ETests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConditionTimeout = TimeSpan.FromSeconds(20);
    private readonly PostgreSqlClusterFixture cluster;
    private readonly ITestOutputHelper output;

    public SimulatorProcessE2ETests(
        PostgreSqlClusterFixture cluster,
        ITestOutputHelper output)
    {
        this.cluster = cluster;
        this.output = output;
    }

    [Fact]
    public async Task ProductionProcessesRecoverAcrossStageCrashDatabaseFaultServerRestartAndSlowConsumer()
    {
        var measured = Stopwatch.StartNew();
        var database = await cluster.CreateDatabaseAsync();
        var processIds = new List<int>();
        var processes = new List<ManagedDispatcherProcess>();
        TcpFaultProxy? proxy = null;
        Exception? primaryFailure = null;
        string? serverPublishDirectory = null;
        try
        {
            output.WriteLine("C07 phase: migrations.");
            await RunMigratorProcessAsync(database, processes, processIds);
            serverPublishDirectory = await PublishServerAsync(processes, processIds);
            var serverExecutable = Path.Combine(
                serverPublishDirectory,
                "Dispatcher.Server.dll");
            await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            var fixture = await CreateProductionFixtureAsync(dataSource);

            var runtimeConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                Pooling = false,
            }.ConnectionString;
            var runtimeEnvironment = RuntimeEnvironment(runtimeConnection, fixture);

            await using var alarmLockConnection = new NpgsqlConnection(database.ConnectionString);
            await alarmLockConnection.OpenAsync();
            await using var alarmLock = await alarmLockConnection.BeginTransactionAsync();
            await using (var lockCommand = new NpgsqlCommand(
                             $"SET LOCAL ROLE \"{Dispatcher.Alarm.AlarmMigrations.Owner}\"; " +
                             $"LOCK TABLE {Dispatcher.Alarm.AlarmMigrations.Schema}.definition " +
                             "IN ACCESS EXCLUSIVE MODE;",
                             alarmLockConnection,
                             alarmLock))
            {
                await lockCommand.ExecuteNonQueryAsync();
            }

            var crashedRuntime = StartRuntime(
                runtimeEnvironment,
                [database.ConnectionString, runtimeConnection],
                processes,
                processIds,
                "RuntimeHost-stage-crash");
            output.WriteLine("C07 phase: crash between History and Alarm.");
            await crashedRuntime.WaitForOutputAsync("state=Reconciling", StartupTimeout);
            await WaitUntilAsync(
                "History completed while Alarm remained pending",
                () => HasPartialDeliveryAsync(dataSource, fixture.ScopeId),
                ConditionTimeout,
                crashedRuntime);

            var serverPort = ReserveTcpPort();
            var serverEnvironment = ServerEnvironment(
                database.ConnectionString,
                serverPort,
                serverPublishDirectory);
            var server = StartServer(
                serverExecutable,
                serverEnvironment,
                [database.ConnectionString, fixture.OperatorPassword],
                processes,
                processIds,
                "Server");
            output.WriteLine("C07 phase: same-origin Server before publication.");
            var serverAddress = new Uri($"http://127.0.0.1:{serverPort}/");
            using var http = new HttpClient { BaseAddress = serverAddress };
            await WaitForHttpOkAsync(http, "/", StartupTimeout, server);
            Assert.Contains("<div id=\"app\">", await http.GetStringAsync("/"));
            var session = await LoginAsync(http, fixture);
            SetSessionHeader(http, session.AccessToken);
            var unpublished = await http.GetAsync(
                $"api/runtime/{fixture.ScopeId}/snapshot");
            Assert.NotEqual(HttpStatusCode.OK, unpublished.StatusCode);

            await crashedRuntime.KillAsync();
            await alarmLock.RollbackAsync();
            var runtime = StartRuntime(
                runtimeEnvironment,
                [database.ConnectionString, runtimeConnection],
                processes,
                processIds,
                "RuntimeHost");
            output.WriteLine("C07 phase: RuntimeHost recovery and publication.");
            await runtime.WaitForOutputAsync("state=Reconciling", StartupTimeout);

            var published = await WaitForSnapshotAsync(
                http,
                fixture.ScopeId,
                ConditionTimeout,
                runtime,
                server);
            Assert.Equal(fixture.PointId, Assert.Single(published.Points).PointId);
            Assert.InRange(published.Points[0].Value, 140L, 160L);
            var completed = await ReadPipelineAsync(dataSource, fixture.ScopeId);
            Assert.True(completed.HistoryBatches >= 1);
            Assert.Equal(completed.HistoryBatches, completed.HistorySamples);
            Assert.Equal(1L, completed.AlarmOccurrences);
            Assert.Equal(1L, completed.EventJournal);
            Assert.Equal(1L, completed.EventProjection);
            Assert.InRange(completed.UnfinishedDeliveries, 0L, 1L);

            await VerifyDeltaAndGapResnapshotAsync(
                serverAddress,
                session.AccessToken,
                fixture.ScopeId,
                dataSource,
                runtime,
                server);
            output.WriteLine("C07 phase: SignalR delta, gap and resnapshot.");

            await server.ShutdownAsync(TimeSpan.FromSeconds(10));
            server = StartServer(
                serverExecutable,
                serverEnvironment,
                [database.ConnectionString, fixture.OperatorPassword, session.AccessToken],
                processes,
                processIds,
                "Server-restarted");
            output.WriteLine("C07 phase: Server restart.");
            await WaitForHttpOkAsync(http, "/", StartupTimeout, server);
            Assert.Equal(
                HttpStatusCode.OK,
                (await http.GetAsync("api/auth/bootstrap")).StatusCode);
            Assert.Equal(
                HttpStatusCode.OK,
                (await http.GetAsync($"api/runtime/{fixture.ScopeId}/snapshot")).StatusCode);

            await runtime.ShutdownAsync(TimeSpan.FromSeconds(10));
            var target = new NpgsqlConnectionStringBuilder(database.ConnectionString);
            proxy = TcpFaultProxy.Start(IPAddress.Loopback, target.Port);
            runtimeConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                Host = IPAddress.Loopback.ToString(),
                Port = proxy.Port,
                ApplicationName = "dispatcher-c07-runtime-fault",
                Pooling = false,
                Timeout = 2,
                CommandTimeout = 5,
            }.ConnectionString;
            runtime = StartRuntime(
                RuntimeEnvironment(runtimeConnection, fixture),
                [database.ConnectionString, runtimeConnection],
                processes,
                processIds,
                "RuntimeHost-database-fault");
            await runtime.WaitForOutputAsync("state=Reconciling", StartupTimeout);

            var beforeFault = await ReadPipelineAsync(dataSource, fixture.ScopeId);
            output.WriteLine("C07 phase: controlled PostgreSQL connection fault.");
            await proxy.PauseAsync();
            await runtime.WaitForOutputAsync(
                "reason=runtime.transient_failure",
                ConditionTimeout);
            Assert.False(runtime.HasExited);
            proxy.Resume();
            await runtime.WaitForOutputAsync("session=2", ConditionTimeout);
            await WaitUntilAsync(
                "Runtime publication resumed after the controlled database fault",
                async () =>
                    (await ReadPipelineAsync(dataSource, fixture.ScopeId)).PublishedPosition >
                    beforeFault.PublishedPosition,
                ConditionTimeout,
                runtime);

            var recovered = await ReadPipelineAsync(dataSource, fixture.ScopeId);
            Assert.Equal(recovered.HistoryBatches, recovered.HistorySamples);
            Assert.Equal(1L, recovered.AlarmOccurrences);
            Assert.Equal(1L, recovered.EventJournal);
            Assert.Equal(1L, recovered.EventProjection);
            Assert.InRange(recovered.UnfinishedDeliveries, 0L, 1L);

            await runtime.ShutdownAsync(TimeSpan.FromSeconds(10));
            await server.ShutdownAsync(TimeSpan.FromSeconds(10));
            Assert.True(runtime.OutputContains("state=Stopped"));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            output.WriteLine($"C07 primary failure: {exception}");
            foreach (var process in processes)
            {
                output.WriteLine(process.Diagnostics());
            }
            throw;
        }
        finally
        {
            if (proxy is not null)
            {
                await proxy.DisposeAsync();
            }
            for (var index = processes.Count - 1; index >= 0; index--)
            {
                await processes[index].DisposeAsync();
            }
            try
            {
                await DropDatabaseWithRetryAsync(database);
            }
            catch (Exception cleanupFailure) when (primaryFailure is not null)
            {
                output.WriteLine($"C07 database cleanup also failed: {cleanupFailure}");
            }
            if (serverPublishDirectory is not null)
            {
                DeletePublishDirectory(serverPublishDirectory);
            }
        }

        foreach (var processId in processIds)
        {
            Assert.False(IsProcessRunning(processId), $"Process {processId} remained after C07 cleanup.");
        }
        Assert.False(await DatabaseExistsAsync(database.ConnectionString, database.Name));
        measured.Stop();
        output.WriteLine($"C07 process corpus duration: {measured.Elapsed}.");
        Assert.True(measured.Elapsed < TimeSpan.FromMinutes(3));
    }

    private static async Task RunMigratorProcessAsync(
        TestDatabase database,
        List<ManagedDispatcherProcess> processes,
        List<int> processIds)
    {
        var environment = new Dictionary<string, string>
        {
            [MigrationEnvironmentVariables.ConnectionString] = database.ConnectionString,
            [MigrationEnvironmentVariables.GetRoleVariableName(
                CoreRuntimeMigrations.PublishedReadRoleKey)] =
                PostgreSqlClusterFixture.OwnerARole,
        };
        foreach (var registration in MigrationCatalog.Registrations)
        {
            environment[MigrationEnvironmentVariables.GetRoleVariableName(registration.Owner)] =
                registration.Owner;
        }

        var migrator = ManagedDispatcherProcess.StartDotNet(
            "DatabaseMigrator",
            Executable("Dispatcher.DatabaseMigrator"),
            environment,
            [database.ConnectionString],
            clearEnvironmentPrefixes: ["DISPATCHER_MIGRATIONS_"]);
        processes.Add(migrator);
        processIds.Add(migrator.Id);
        await migrator.WaitForSuccessfulExitAsync(StartupTimeout);
        Assert.True(migrator.OutputContains("Production database migration succeeded."));
    }

    private static ManagedDispatcherProcess StartRuntime(
        IReadOnlyDictionary<string, string> environment,
        IEnumerable<string> secrets,
        List<ManagedDispatcherProcess> processes,
        List<int> processIds,
        string name)
    {
        var process = ManagedDispatcherProcess.StartDotNet(
            name,
            Executable("Dispatcher.RuntimeHost"),
            environment,
            secrets,
            clearEnvironmentPrefixes: ["DISPATCHER_RUNTIME_", "DISPATCHER_PROCESS_CONTROL_"]);
        processes.Add(process);
        processIds.Add(process.Id);
        return process;
    }

    private static ManagedDispatcherProcess StartServer(
        string serverExecutable,
        IReadOnlyDictionary<string, string> environment,
        IEnumerable<string> secrets,
        List<ManagedDispatcherProcess> processes,
        List<int> processIds,
        string name)
    {
        var process = ManagedDispatcherProcess.StartDotNet(
            name,
            serverExecutable,
            environment,
            secrets,
            clearEnvironmentPrefixes: ["ASPNETCORE_", "ConnectionStrings__", "Dispatcher__"]);
        processes.Add(process);
        processIds.Add(process.Id);
        return process;
    }

    private static async Task<string> PublishServerAsync(
        List<ManagedDispatcherProcess> processes,
        List<int> processIds)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dispatcher-c07-server-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var root = FindProjectRoot();
        var project = Path.Combine(
            root,
            "src",
            "Dispatcher.Server",
            "Dispatcher.Server.csproj");
        var publish = ManagedDispatcherProcess.StartDotNetCommand(
            "Server-publish",
            [
                "publish",
                project,
                "--configuration",
                BuildConfiguration(),
                "--no-build",
                "--no-restore",
                "--output",
                directory,
            ],
            root,
            new Dictionary<string, string>(),
            []);
        processes.Add(publish);
        processIds.Add(publish.Id);
        await publish.WaitForSuccessfulExitAsync(StartupTimeout);
        return directory;
    }

    private static async Task<ProductionFixture> CreateProductionFixtureAsync(
        NpgsqlDataSource dataSource)
    {
        var scopeId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var pointId = Guid.NewGuid();
        var facilityScope = FacilityScopeId.From(scopeId);
        var identityScope = IdentityScopeId.From(scopeId);
        var point = PointId.From(pointId);
        var clock = SystemClock.Instance;
        var identity = new IdentityStore(
            dataSource,
            IdentityMigrations.Owner,
            clock,
            new IdentitySecurityPolicy(
                10_000,
                12,
                128,
                3,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(30),
                TimeSpan.FromHours(2)));
        var administratorPassword = RandomPassword();
        var administratorUserName = $"admin-{Guid.NewGuid():N}";
        Assert.True((await identity.BootstrapAdministratorAsync(new(
            IdentityAccountId.New(),
            SubjectId.New(),
            null,
            IdentityRoleId.New(),
            administratorUserName,
            administratorPassword))).IsSuccess);
        var administrator = (await identity.LoginAsync(new(
            administratorUserName,
            administratorPassword))).Value;
        var mutation = SessionAuthorization.AuthorizeMutation(
            administrator.Session,
            IdentityPermissions.Administer,
            clock).Value;

        var grants = new List<IdentityPermissionGrant>
        {
            new(RuntimePermissions.ReadCurrent, identityScope),
            new(RuntimePermissions.ReadPoint(point), identityScope),
            new(ConfigurationPermissions.Read(facilityScope), identityScope),
            new(ConfigurationPermissions.Save(facilityScope), identityScope),
            new(ConfigurationPermissions.Validate(facilityScope), identityScope),
            new(ConfigurationPermissions.Publish(facilityScope), identityScope),
            new(ConfigurationPermissions.Distribute(facilityScope), identityScope),
            new(ConfigurationPermissions.Activate(facilityScope), identityScope),
        };
        var roleId = IdentityRoleId.New();
        var accountId = IdentityAccountId.New();
        var operatorUserName = $"operator-{Guid.NewGuid():N}";
        var operatorPassword = RandomPassword();
        Assert.True((await identity.CreateScopeAsync(
            mutation,
            new(identityScope, "Process E2E"))).IsSuccess);
        Assert.True((await identity.CreateRoleAsync(
            mutation,
            new(roleId, "Process operators", grants))).IsSuccess);
        Assert.True((await identity.CreateAccountAsync(
            mutation,
            new(
                accountId,
                SubjectId.New(),
                null,
                identityScope,
                operatorUserName,
                operatorPassword))).IsSuccess);
        Assert.True((await identity.AssignRoleToAccountAsync(
            mutation,
            accountId,
            roleId)).IsSuccess);
        var operatorSession = (await identity.LoginAsync(
            new(operatorUserName, operatorPassword))).Value.Session;

        var manifest = JsonSerializer.Serialize(new
        {
            simulator = new
            {
                sourceId,
                seed = 17UL,
                points = new[]
                {
                    new
                    {
                        pointId,
                        baseline = 150L,
                        amplitude = 10L,
                        unit = "kW",
                    },
                },
            },
        });
        var configuration = new ConfigurationService(
            new ConfigurationStore(
                dataSource,
                ConfigurationMigrations.Owner,
                clock),
            clock);
        var draft = (await configuration.SaveAsync(
            operatorSession,
            facilityScope,
            new(manifest, [], null))).Value;
        var validated = (await configuration.ValidateAsync(
            operatorSession,
            facilityScope,
            draft.RevisionId,
            draft.Version)).Value;
        _ = (await configuration.PublishAsync(
            operatorSession,
            facilityScope,
            new(validated.RevisionId, validated.Version, []))).Value;
        var distribution = (await configuration.ClaimDistributionAsync(
            operatorSession,
            facilityScope,
            "process-distributor",
            TimeSpan.FromMinutes(1))).Value;
        var distributed = (await configuration.CompleteDistributionAsync(
            operatorSession,
            facilityScope,
            distribution.JobId,
            "process-distributor")).Value;
        var simulator = new SimulatorRuntimeStore(
            dataSource,
            SimulatorRuntimeMigrations.Owner,
            clock);
        var activated = await new SimulatorReleaseActivator(configuration, simulator)
            .ActivateDesiredAsync(
                operatorSession,
                facilityScope,
                "process-activator");
        Assert.True(activated.IsSuccess);
        Assert.Equal(distributed.RevisionId, activated.Value.Receipt.RevisionId);

        var alarms = new AlarmStore(dataSource, AlarmMigrations.Owner, clock);
        Assert.True((await alarms.ActivateDefinitionSetAsync(new(
            RuntimeScopeId.From(scopeId),
            RevisionNumber.Initial,
            [
                new AlarmDefinition(
                    AlarmDefinitionId.New(),
                    point,
                    "High process value",
                    AlarmThresholdDirection.High,
                    threshold: 100,
                    hysteresis: 0,
                    raiseDelay: TimeSpan.Zero,
                    clearDelay: TimeSpan.Zero,
                    priority: AlarmPriority.High),
            ]))).IsSuccess);

        return new ProductionFixture(
            scopeId,
            pointId,
            distributed.RevisionId.Value,
            operatorUserName,
            operatorPassword);
    }

    private static Dictionary<string, string> RuntimeEnvironment(
        string connectionString,
        ProductionFixture fixture) =>
        new Dictionary<string, string>
        {
            ["DISPATCHER_PROCESS_CONTROL_STDIN"] = "1",
            ["DISPATCHER_RUNTIME_SCOPE_ID"] = fixture.ScopeId.ToString(),
            ["DISPATCHER_RUNTIME_WORKLOAD_IDENTITY"] = "process-e2e-runtime",
            ["DISPATCHER_RUNTIME_CONNECTION_STRING"] = connectionString,
            ["DISPATCHER_RUNTIME_DATABASE_ROLE"] = CoreRuntimeMigrations.Owner,
            ["DISPATCHER_RUNTIME_SIMULATOR_DATABASE_ROLE"] = SimulatorRuntimeMigrations.Owner,
            ["DISPATCHER_RUNTIME_MAX_CURRENT_POINTS"] = "8",
            ["DISPATCHER_RUNTIME_RETAINED_CURRENT_CHANGES"] = "3",
            ["DISPATCHER_RUNTIME_INGRESS_CAPACITY"] = "4",
            ["DISPATCHER_RUNTIME_MAX_PROTOCOL_SOURCES"] = "1",
            ["DISPATCHER_RUNTIME_POLL_INTERVAL_MS"] = "200",
            ["DISPATCHER_RUNTIME_POLL_TIMEOUT_MS"] = "1000",
            ["DISPATCHER_RUNTIME_SCHEDULER_MAX_BINDINGS"] = "1",
            ["DISPATCHER_RUNTIME_SCHEDULER_MAX_IN_FLIGHT"] = "1",
            ["DISPATCHER_RUNTIME_RECONCILIATION_INITIAL_BACKOFF_MS"] = "50",
            ["DISPATCHER_RUNTIME_RECONCILIATION_MAX_BACKOFF_MS"] = "250",
            ["DISPATCHER_RUNTIME_HISTORY_DATABASE_ROLE"] = Dispatcher.History.HistoryMigrations.Owner,
            ["DISPATCHER_RUNTIME_ALARM_DATABASE_ROLE"] = AlarmMigrations.Owner,
            ["DISPATCHER_RUNTIME_EVENT_DATABASE_ROLE"] = Dispatcher.Events.EventMigrations.Owner,
            ["DISPATCHER_RUNTIME_CONFIGURATION_REVISION_ID"] =
                fixture.ConfigurationRevisionId.ToString(),
            ["DISPATCHER_RUNTIME_ALARM_DEFINITION_EPOCH"] = "1",
            ["DISPATCHER_RUNTIME_HISTORY_MAX_PAGE_SIZE"] = "100",
            ["DISPATCHER_RUNTIME_HISTORY_MAX_AGGREGATE_BUCKETS"] = "100",
            ["DISPATCHER_RUNTIME_EVENT_MAX_PAGE_SIZE"] = "100",
            ["DISPATCHER_RUNTIME_EVENT_RETAINED_PROJECTION_CHANGES"] = "100",
            ["DISPATCHER_RUNTIME_EVENT_MAX_FEED_CHANGES"] = "100",
            ["DISPATCHER_RUNTIME_DOWNSTREAM_MAX_ATTEMPTS"] = "3",
            ["DISPATCHER_RUNTIME_DOWNSTREAM_INITIAL_BACKOFF_MS"] = "25",
            ["DISPATCHER_RUNTIME_DOWNSTREAM_MAX_BACKOFF_MS"] = "100",
        };

    private static Dictionary<string, string> ServerEnvironment(
        string connectionString,
        int port,
        string contentRoot) =>
        new Dictionary<string, string>
        {
            ["DISPATCHER_PROCESS_CONTROL_STDIN"] = "1",
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
            ["ASPNETCORE_CONTENTROOT"] = contentRoot,
            ["ConnectionStrings__Dispatcher"] = connectionString,
            ["Dispatcher__TestSessionBridge__Enabled"] = "false",
            ["Dispatcher__Core__PublishedReadRole"] = PostgreSqlClusterFixture.OwnerARole,
            ["Dispatcher__Core__MaxSnapshotPoints"] = "100",
            ["Dispatcher__Core__MaxDeltaChanges"] = "100",
            ["Dispatcher__Identity__DatabaseRole"] = IdentityMigrations.Owner,
            ["Dispatcher__Identity__PasswordIterations"] = "10000",
            ["Dispatcher__Identity__PasswordMinimumLength"] = "12",
            ["Dispatcher__Identity__PasswordMaximumLength"] = "128",
            ["Dispatcher__Identity__MaximumFailedAttempts"] = "3",
            ["Dispatcher__Identity__LockoutSeconds"] = "300",
            ["Dispatcher__Identity__AccessLifetimeSeconds"] = "1800",
            ["Dispatcher__Identity__RefreshLifetimeSeconds"] = "7200",
        };

    private static async Task<Dispatcher.Server.ProductionSessionPayload> LoginAsync(
        HttpClient http,
        ProductionFixture fixture)
    {
        var response = await http.PostAsJsonAsync(
            "api/auth/login",
            new
            {
                UserName = fixture.OperatorUserName,
                Password = fixture.OperatorPassword,
            });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<
            Dispatcher.Server.ProductionSessionPayload>())!;
    }

    private static async Task<RuntimeSnapshotPayload> WaitForSnapshotAsync(
        HttpClient http,
        Guid scopeId,
        TimeSpan timeout,
        params ManagedDispatcherProcess[] processes)
    {
        RuntimeSnapshotPayload? snapshot = null;
        await WaitUntilAsync(
            "Published runtime snapshot",
            async () =>
            {
                try
                {
                    var response = await http.GetAsync($"api/runtime/{scopeId}/snapshot");
                    if (!response.IsSuccessStatusCode)
                    {
                        return false;
                    }
                    snapshot = await response.Content.ReadFromJsonAsync<RuntimeSnapshotPayload>();
                    return snapshot is not null && snapshot.Points.Count > 0;
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            },
            timeout,
            processes);
        return snapshot!;
    }

    private static async Task VerifyDeltaAndGapResnapshotAsync(
        Uri serverAddress,
        string accessToken,
        Guid scopeId,
        NpgsqlDataSource dataSource,
        params ManagedDispatcherProcess[] processes)
    {
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(serverAddress, "hubs/runtime"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.Headers["Authorization"] =
                    $"Dispatcher-Session {accessToken}";
            })
            .Build();
        await hub.StartAsync();
        var snapshot = await hub.InvokeAsync<RuntimeSnapshotPayload>("Bootstrap", scopeId);
        Assert.Single(snapshot.Points);

        RealtimePollPayload? delta = null;
        await WaitUntilAsync(
            "SignalR delta",
            async () =>
            {
                var next = await hub.InvokeAsync<RealtimePollPayload>(
                    "Poll", scopeId, snapshot.Cursor);
                if (next.Kind == RealtimePollKind.Delta)
                {
                    delta = next;
                    return true;
                }
                Assert.NotEqual(RealtimePollKind.Gap, next.Kind);
                return false;
            },
            ConditionTimeout,
            processes);
        var webCursor = delta!.Delta!.To;
        var beforeSlowConsumer = await ReadPipelineAsync(dataSource, scopeId);
        await WaitUntilAsync(
            "Delta retention advances beyond the slow consumer",
            async () =>
                (await ReadPipelineAsync(dataSource, scopeId)).PublishedPosition >=
                beforeSlowConsumer.PublishedPosition + 6,
            ConditionTimeout,
            processes);
        var gap = await hub.InvokeAsync<RealtimePollPayload>("Poll", scopeId, webCursor);
        Assert.Equal(RealtimePollKind.Gap, gap.Kind);
        var resnapshot = await hub.InvokeAsync<RuntimeSnapshotPayload>("Bootstrap", scopeId);
        Assert.Single(resnapshot.Points);
    }

    private static async Task<bool> HasPartialDeliveryAsync(
        NpgsqlDataSource dataSource,
        Guid scopeId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""
            SELECT EXISTS (
                SELECT 1
                FROM {CoreRuntimeMigrations.Schema}.processing_delivery
                WHERE scope_id=@scope
                  AND stage=1
                  AND history_state=2
                  AND alarm_state=1);
            """,
            connection);
        command.Parameters.AddWithValue("scope", scopeId);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task<PipelineSnapshot> ReadPipelineAsync(
        NpgsqlDataSource dataSource,
        Guid scopeId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""
            SELECT
                COALESCE((SELECT current_position
                          FROM {CoreRuntimeMigrations.Schema}.published_scope
                          WHERE scope_id=@scope), 0),
                (SELECT count(*) FROM {Dispatcher.History.HistoryMigrations.Schema}.ingest_batch
                 WHERE scope_id=@scope),
                (SELECT count(*) FROM {Dispatcher.History.HistoryMigrations.Schema}.sample
                 WHERE scope_id=@scope),
                (SELECT count(*) FROM {AlarmMigrations.Schema}.occurrence WHERE scope_id=@scope),
                (SELECT count(*) FROM {Dispatcher.Events.EventMigrations.Schema}.journal_event
                 WHERE scope_id=@scope),
                (SELECT count(*) FROM {Dispatcher.Events.EventMigrations.Schema}.occurrence_projection
                 WHERE scope_id=@scope),
                (SELECT count(*) FROM {CoreRuntimeMigrations.Schema}.processing_delivery
                 WHERE scope_id=@scope AND stage < 3);
            """,
            connection);
        command.Parameters.AddWithValue("scope", scopeId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new PipelineSnapshot(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6));
    }

    private static async Task WaitForHttpOkAsync(
        HttpClient http,
        string path,
        TimeSpan timeout,
        params ManagedDispatcherProcess[] processes) =>
        await WaitUntilAsync(
            $"HTTP {path}",
            async () =>
            {
                try
                {
                    return (await http.GetAsync(path)).StatusCode == HttpStatusCode.OK;
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            },
            timeout,
            processes);

    private static async Task WaitUntilAsync(
        string description,
        Func<Task<bool>> condition,
        TimeSpan timeout,
        params ManagedDispatcherProcess[] processes)
    {
        using var bounded = new CancellationTokenSource(timeout);
        while (!bounded.IsCancellationRequested)
        {
            foreach (var process in processes)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"{description} failed because a process exited.{Environment.NewLine}" +
                        string.Join(Environment.NewLine, processes.Select(item => item.Diagnostics())));
                }
            }
            if (await condition())
            {
                return;
            }
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), bounded.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (bounded.IsCancellationRequested)
            {
                break;
            }
        }
        throw new TimeoutException(
            $"{description} was not reached within {timeout}.{Environment.NewLine}" +
            string.Join(Environment.NewLine, processes.Select(item => item.Diagnostics())));
    }

    private static void SetSessionHeader(HttpClient http, string accessToken) =>
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Dispatcher-Session", accessToken);

    private static string RandomPassword() =>
        $"A9!{Convert.ToHexString(RandomNumberGenerator.GetBytes(18))}a";

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string Executable(string projectName)
    {
        var root = FindProjectRoot();
        return Path.Combine(
            root,
            "src",
            projectName,
            "bin",
            BuildConfiguration(),
            "net10.0",
            $"{projectName}.dll");
    }

    private static string BuildConfiguration() =>
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;

    private static string FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Dispatcher.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Dispatcher project root was not found.");
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = DiagnosticsProcess.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> DatabaseExistsAsync(
        string connectionString,
        string databaseName)
    {
        var administration = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
        }.ConnectionString;
        await using var connection = new NpgsqlConnection(administration);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname=@name);",
            connection);
        command.Parameters.AddWithValue("name", databaseName);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task DropDatabaseWithRetryAsync(TestDatabase database)
    {
        Exception? failure = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await database.DisposeAsync();
                return;
            }
            catch (NpgsqlException exception)
            {
                failure = exception;
                if (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250));
                }
            }
        }
        throw new InvalidOperationException(
            "Temporary C07 database cleanup failed after bounded retries.",
            failure);
    }

    private static void DeletePublishDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var tempPath = Path.GetFullPath(Path.GetTempPath());
        if (!fullPath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith(
                "dispatcher-c07-server-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Refusing to delete a directory outside the C07 publish prefix.");
        }
        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private sealed record ProductionFixture(
        Guid ScopeId,
        Guid PointId,
        Guid ConfigurationRevisionId,
        string OperatorUserName,
        string OperatorPassword);

    private sealed record PipelineSnapshot(
        long PublishedPosition,
        long HistoryBatches,
        long HistorySamples,
        long AlarmOccurrences,
        long EventJournal,
        long EventProjection,
        long UnfinishedDeliveries);
}
