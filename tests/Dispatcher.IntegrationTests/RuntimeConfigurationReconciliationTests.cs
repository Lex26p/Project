using System.Text.Json;
using System.Globalization;
using Dispatcher.Alarm;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Events;
using Dispatcher.Facilities;
using Dispatcher.History;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Protocols;
using Dispatcher.RuntimeHost;
using Dispatcher.Semantics;
using Dispatcher.Simulator;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class RuntimeConfigurationReconciliationTests
{
    private static readonly Guid ScopeValue =
        Guid.Parse("e1000000-0000-0000-0000-000000000001");
    private static readonly Guid SourceValue =
        Guid.Parse("e2000000-0000-0000-0000-000000000001");
    private static readonly Guid PointValue =
        Guid.Parse("e3000000-0000-0000-0000-000000000001");
    private static readonly Guid AlarmValue =
        Guid.Parse("e4000000-0000-0000-0000-000000000001");
    private static readonly Guid ModbusSourceValue =
        Guid.Parse("e2000000-0000-0000-0000-000000000012");
    private static readonly Guid ModbusPointValue =
        Guid.Parse("e3000000-0000-0000-0000-000000000012");
    private static readonly Guid ModbusAlarmValue =
        Guid.Parse("e4000000-0000-0000-0000-000000000012");
    private static readonly DateTimeOffset Start =
        new(2026, 7, 28, 8, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public RuntimeConfigurationReconciliationTests(PostgreSqlClusterFixture cluster) =>
        this.cluster = cluster;

    [Fact]
    public async Task SameRuntimeReconcilesNewRevisionAndFencesOldPoll()
    {
        await using var context = await Context.CreateAsync(cluster);
        var revision1 = await context.PublishAsync(10);
        var first = await context.ActivateNextAsync();
        Assert.Equal(revision1.RevisionId, first.RevisionId);
        Assert.Equal(1L, first.RuntimeGeneration);
        Assert.Equal(10L, Poll(first.Source));

        var revision2 = await context.PublishAsync(20);
        var second = await context.ActivateNextAsync();
        Assert.Equal(revision2.RevisionId, second.RevisionId);
        Assert.Equal(2L, second.RuntimeGeneration);
        Assert.Equal(RevisionNumber.From(2), second.AlarmDefinitionEpoch);
        Assert.Equal(20L, Poll(second.Source));
        Assert.Equal(2L, await context.ReadAlarmEpochAsync());

        var runtime = new CoreRuntime(
            RuntimeScopeId.From(ScopeValue),
            context.Clock,
            context.Clock,
            new RuntimeCurrentLimits(8, 8));
        Assert.True(runtime.ActivateBinding(first.Source.Binding).IsSuccess);
        Assert.True(runtime.ActivateBinding(second.Source.Binding).IsSuccess);
        var staleCut = RuntimeCut.Normalize(
            first.Source.Binding,
            1,
            [
                new SourceObservation(
                    RuntimeScopeId.From(ScopeValue),
                    SourceId.From(SourceValue),
                    PointId.From(PointValue),
                    new OwnerPosition<SourceObservation>(1),
                    TypedValue.From(10L),
                    Unit.FromSymbol("kW"),
                    DataQuality.Good,
                    Freshness.Fresh,
                    SourceTimestamp.FromUtc(Start)),
            ]).Value;
        Assert.Equal("core.binding_stale", runtime.Apply(staleCut).Error?.Code.Value);

        var state = (await context.Configuration.ReadScopeAsync(context.Session, context.ScopeId)).Value;
        Assert.Equal(revision2.RevisionId, state.DistributedRevisionId);
        Assert.Equal(revision2.RevisionId, state.ActivatedRevisionId);
        Assert.Equal(revision2.RevisionId.Value, context.Definitions.Read().ConfigurationRevisionId);
    }

    [Fact]
    public async Task ProductionSessionPublishesNewRevisionWithoutRestart()
    {
        await using var context = await Context.CreateAsync(cluster);
        var firstRevision = await context.PublishAsync(10);
        var options = context.CreateRuntimeOptions(firstRevision.RevisionId);
        await using var session = ProductionRuntimeHostSession.Create(
            options,
            context.Clock,
            context.Clock);
        Assert.True((await session.StartAsync(CancellationToken.None)).IsSuccess);
        using var cancellation = new CancellationTokenSource();
        var running = session.RunSimulatorCycleAsync(cancellation.Token);

        var first = await context.WaitForCurrentAsync(10, 1, TimeSpan.FromSeconds(10));
        Assert.Equal(10L, Assert.Single(first.Current).Value.Value);
        _ = await context.PublishAsync(20);
        var second = await context.WaitForCurrentAsync(20, 2, TimeSpan.FromSeconds(10));
        Assert.Equal(20L, Assert.Single(second.Current).Value.Value);
        Assert.True(second.CurrentPosition > first.CurrentPosition);

        cancellation.Cancel();
        var stoppedWorker = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stoppedWorker.IsSuccess, stoppedWorker.Error?.Code.Value);
        Assert.Equal(RuntimeHostSessionCycleStatus.WorkerStopped, stoppedWorker.Value.Status);
        Assert.True((await session.StopAsync(CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task ProductionModbusPublishesCurrentHistoryAlarmEventAndRecoversQuality()
    {
        var failNext = 0;
        var rawValue = 21;
        var failureSeen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new FakeModbusTcpPeer(
            async (request, _, cancellationToken) =>
            {
                if (Interlocked.Exchange(ref failNext, 0) == 1)
                {
                    failureSeen.TrySetResult();
                    return null;
                }

                if (failureSeen.Task.IsCompleted && !resume.Task.IsCompleted)
                {
                    await resume.Task.WaitAsync(cancellationToken);
                }

                var value = checked((ushort)Volatile.Read(ref rawValue));
                return FakeModbusTcpPeer.Response(
                    request.Span,
                    checked((byte)(value >> 8)),
                    checked((byte)value));
            });
        await using var context = await Context.CreateAsync(cluster);
        var revision = await context.PublishRawAsync(
            Context.CreateModbusManifest(peer.Port));
        var options = context.CreateRuntimeOptions(revision.RevisionId) with
        {
            SchedulerMaxBindings = 2,
            ProtocolMaxObservations = 4,
            ModbusLimits = new(4, 8),
        };
        await using var session = ProductionRuntimeHostSession.Create(
            options,
            context.Clock,
            context.Clock);
        Assert.True((await session.StartAsync(CancellationToken.None)).IsSuccess);
        using var cancellation = new CancellationTokenSource();
        var running = session.RunSimulatorCycleAsync(cancellation.Token);

        var good = await context.WaitForPointAsync(
            ModbusPointValue,
            item =>
                item.Value.Value == 42 &&
                item.Quality == DataQuality.Good &&
                item.Freshness == Freshness.Fresh,
            TimeSpan.FromSeconds(10));
        Assert.Equal(SourceBindingGeneration.From(1), good.BindingGeneration);

        Interlocked.Exchange(ref failNext, 1);
        await failureSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var unavailable = await context.WaitForPointAsync(
            ModbusPointValue,
            item =>
                item.Quality == DataQuality.Bad &&
                item.Freshness == Freshness.Stale,
            TimeSpan.FromSeconds(10));
        Assert.Equal(42L, unavailable.Value.Value);

        Volatile.Write(ref rawValue, 43);
        resume.TrySetResult();
        var recovered = await context.WaitForPointAsync(
            ModbusPointValue,
            item =>
                item.Value.Value == 86 &&
                item.Quality == DataQuality.Good &&
                item.Freshness == Freshness.Fresh,
            TimeSpan.FromSeconds(10));
        Assert.True(recovered.SourcePosition.Value > unavailable.SourcePosition.Value);
        await context.WaitForModbusPipelineAsync(
            ModbusPointValue,
            TimeSpan.FromSeconds(10));
        Assert.NotEmpty(peer.FunctionCodes);
        Assert.All(peer.FunctionCodes, code => Assert.Equal((byte)3, code));

        cancellation.Cancel();
        var stoppedWorker = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stoppedWorker.IsSuccess, stoppedWorker.Error?.Code.Value);
        Assert.True((await session.StopAsync(CancellationToken.None)).IsSuccess);
    }

    [Theory]
    [InlineData(RuntimeConfigurationActivationPoint.Claimed)]
    [InlineData(RuntimeConfigurationActivationPoint.Prepared)]
    [InlineData(RuntimeConfigurationActivationPoint.Switched)]
    public async Task CrashRecoveryDoesNotDuplicateSwitch(
        RuntimeConfigurationActivationPoint crashPoint)
    {
        await using var context = await Context.CreateAsync(cluster);
        var revision = await context.PublishAsync(42);
        var crashing = context.CreateReconciler(new RuntimeConfigurationActivationHook(
            (point, _) => point == crashPoint
                ? Task.FromException(new InvalidOperationException("simulated crash"))
                : Task.CompletedTask));

        if (crashPoint == RuntimeConfigurationActivationPoint.Switched)
        {
            var prepared = await crashing.PrepareNextAsync();
            Assert.Equal(RuntimeConfigurationReconciliationStatus.Prepared, prepared.Value.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => crashing.CommitAsync(prepared.Value.Prepared!));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => crashing.PrepareNextAsync());
        }

        context.Clock.Advance(TimeSpan.FromSeconds(31));
        var recovered = context.CreateReconciler();
        var reconciliation = await recovered.PrepareNextAsync();
        Assert.Equal(RuntimeConfigurationReconciliationStatus.Prepared, reconciliation.Value.Status);
        Assert.Equal(2, reconciliation.Value.Prepared!.Claim.Attempts);
        var active = await recovered.CommitAsync(reconciliation.Value.Prepared);
        Assert.True(active.IsSuccess, active.Error?.Code.Value);
        Assert.Equal(revision.RevisionId, active.Value.RevisionId);
        Assert.Equal(1L, active.Value.RuntimeGeneration);
        Assert.Equal(1L, await context.CountSimulatorActionsAsync("manifest.activate"));
        Assert.Equal(1L, await context.CountWorkloadActionsAsync("workload.switch"));
    }

    [Fact]
    public async Task InvalidManifestIsRejectedWhilePreviousGenerationStaysActive()
    {
        await using var context = await Context.CreateAsync(cluster);
        var firstRevision = await context.PublishAsync(10);
        var first = await context.ActivateNextAsync();
        var invalid = JsonSerializer.Serialize(new
        {
            simulator = new
            {
                sourceId = "not-a-guid",
                seed = 1,
                points = Array.Empty<object>(),
            },
        });
        var invalidRevision = await context.PublishRawAsync(invalid);

        var rejected = await context.Reconciler.PrepareNextAsync();
        Assert.Equal(RuntimeConfigurationReconciliationStatus.Rejected, rejected.Value.Status);
        var stillActive = await context.Simulator.ReadActiveAsync(context.ScopeId);
        Assert.Equal(firstRevision.RevisionId, stillActive.Value.Receipt.RevisionId);
        Assert.Equal(first.RuntimeGeneration, stillActive.Value.Generation);
        var outcome = await context.Deployments.ReadOutcomeAsync(invalidRevision.RevisionId);
        Assert.Equal("simulator.manifest_invalid", outcome.Value.OutcomeCode);
        Assert.Equal("Runtime configuration validation failed.", outcome.Value.OutcomeMessage);
    }

    [Fact]
    public async Task InvalidReleaseFingerprintIsRejectedWithoutReceivingCandidate()
    {
        await using var context = await Context.CreateAsync(cluster);
        var firstRevision = await context.PublishAsync(10);
        _ = await context.ActivateNextAsync();
        var candidate = await context.PublishAsync(20);
        await context.ReplaceReleaseFingerprintAsync(candidate.RevisionId, new string('C', 64));

        var rejected = await context.Reconciler.PrepareNextAsync();
        Assert.Equal(RuntimeConfigurationReconciliationStatus.Rejected, rejected.Value.Status);
        var active = await context.Simulator.ReadActiveAsync(context.ScopeId);
        Assert.Equal(firstRevision.RevisionId, active.Value.Receipt.RevisionId);
        var outcome = await context.Deployments.ReadOutcomeAsync(candidate.RevisionId);
        Assert.Equal("runtime.configuration_fingerprint", outcome.Value.OutcomeCode);
        Assert.Equal("Configuration fingerprints did not match.", outcome.Value.OutcomeMessage);
    }

    [Fact]
    public async Task ExpiredWorkerLeaseCannotPrepareAfterAnotherWorkerClaims()
    {
        await using var context = await Context.CreateAsync(cluster);
        _ = await context.PublishAsync(10);
        var first = await context.Deployments.ClaimNextAsync(
            context.ScopeId,
            "runtime-a",
            TimeSpan.FromSeconds(10));
        context.Clock.Advance(TimeSpan.FromSeconds(11));
        var second = await context.Deployments.ClaimNextAsync(
            context.ScopeId,
            "runtime-b",
            TimeSpan.FromSeconds(10));

        Assert.Equal(first.Value.JobId, second.Value.JobId);
        Assert.Equal(2, second.Value.Attempts);
        Assert.Equal(
            "configuration.workload_lease_invalid",
            (await context.Deployments.MarkPreparedAsync(first.Value)).Error?.Code.Value);
        Assert.True((await context.Deployments.RejectAsync(
            second.Value,
            "test.cleanup",
            "Test deployment rejected.")).IsSuccess);
    }

    private static long Poll(SimulatorPollingSource source)
    {
        var scheduler = new BoundedPollScheduler(
            RuntimeScopeId.From(ScopeValue),
            new PollScheduleLimits(TimeSpan.FromSeconds(1), 1, 1),
            new MutableClock(Start));
        Assert.True(scheduler.ActivateBinding(source.Binding).IsSuccess);
        var started = scheduler.TryStart(source.Binding, 1);
        Assert.Equal(PollStartStatus.Started, started.Value.Status);
        return Assert.Single(source.Poll(started.Value.Attempt!)).Value.Value;
    }

    private sealed class Context : IAsyncDisposable
    {
        private long sessionGeneration;

        private Context(
            TestDatabase database,
            NpgsqlDataSource dataSource,
            MutableClock clock,
            SessionSnapshot session)
        {
            Database = database;
            DataSource = dataSource;
            Clock = clock;
            Session = session;
            ScopeId = FacilityScopeId.From(ScopeValue);
            Configuration = new ConfigurationService(
                new ConfigurationStore(
                    dataSource,
                    PostgreSqlClusterFixture.OwnerARole,
                    clock),
                clock);
            Deployments = new ConfigurationWorkloadDeploymentStore(
                dataSource,
                PostgreSqlClusterFixture.OwnerARole,
                clock);
            Simulator = new SimulatorRuntimeStore(
                dataSource,
                PostgreSqlClusterFixture.OwnerARole,
                clock);
            Alarms = new AlarmStore(
                dataSource,
                PostgreSqlClusterFixture.OwnerARole,
                clock);
            Definitions = new RuntimeDefinitionBindingState(
                Guid.Parse("e5000000-0000-0000-0000-000000000001"),
                RevisionNumber.Initial);
            Reconciler = CreateReconciler();
        }

        public TestDatabase Database { get; }
        public NpgsqlDataSource DataSource { get; }
        public MutableClock Clock { get; }
        public SessionSnapshot Session { get; }
        public FacilityScopeId ScopeId { get; }
        public ConfigurationService Configuration { get; }
        public ConfigurationWorkloadDeploymentStore Deployments { get; }
        public SimulatorRuntimeStore Simulator { get; }
        public AlarmStore Alarms { get; }
        public RuntimeDefinitionBindingState Definitions { get; }
        public RuntimeConfigurationReconciler Reconciler { get; }

        public static async Task<Context> CreateAsync(PostgreSqlClusterFixture cluster)
        {
            var database = await cluster.CreateDatabaseAsync();
            var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                ConfigurationMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                SimulatorRuntimeMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                AlarmMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                CoreRuntimeMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                HistoryMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
            await PostgresMigrationRunner.ApplyAsync(
                dataSource,
                EventMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
            var clock = new MutableClock(Start);
            var scopeId = FacilityScopeId.From(ScopeValue);
            var session = new SessionSnapshot(
                SessionId.New(),
                SubjectId.New(),
                PrincipalKind.User,
                Start.AddMinutes(-1),
                Start.AddHours(4),
                new EffectivePermissions(
                [
                    ConfigurationPermissions.Read(scopeId),
                    ConfigurationPermissions.Save(scopeId),
                    ConfigurationPermissions.Validate(scopeId),
                    ConfigurationPermissions.Publish(scopeId),
                ]));
            return new Context(database, dataSource, clock, session);
        }

        public RuntimeConfigurationReconciler CreateReconciler(
            RuntimeConfigurationActivationHook? hook = null) =>
            new(
                ScopeId,
                ProtocolWorkloadIdentity.From("runtime-c11").Value,
                TimeSpan.FromSeconds(30),
                Deployments,
                Simulator,
                Alarms,
                (_, _, _) => Task.FromResult(
                    SourceSessionGeneration.From(
                        checked((ulong)Interlocked.Increment(ref sessionGeneration)))),
                Definitions,
                Clock,
                hook);

        public Task<ConfigurationRevisionSnapshot> PublishAsync(long baseline) =>
            PublishRawAsync(CreateManifest(baseline));

        public async Task<ConfigurationRevisionSnapshot> PublishRawAsync(string manifest)
        {
            var draft = (await Configuration.SaveAsync(
                Session,
                ScopeId,
                new SaveConfigurationRequest(manifest, [], null))).Value;
            var validated = (await Configuration.ValidateAsync(
                Session,
                ScopeId,
                draft.RevisionId,
                draft.Version)).Value;
            return (await Configuration.PublishAsync(
                Session,
                ScopeId,
                new PublishConfigurationRequest(
                    validated.RevisionId,
                    validated.Version,
                    []))).Value;
        }

        public async Task<ActivatedRuntimeConfiguration> ActivateNextAsync()
        {
            var prepared = await Reconciler.PrepareNextAsync();
            Assert.Equal(RuntimeConfigurationReconciliationStatus.Prepared, prepared.Value.Status);
            var active = await Reconciler.CommitAsync(prepared.Value.Prepared!);
            Assert.True(active.IsSuccess, active.Error?.Code.Value);
            return active.Value;
        }

        public RuntimeHostOptions CreateRuntimeOptions(ConfigurationRevisionId initialRevision) =>
            new(
                RuntimeScopeId.From(ScopeValue),
                ProtocolWorkloadIdentity.From("runtime-c11-production"),
                Database.ConnectionString,
                PostgreSqlClusterFixture.OwnerARole,
                PostgreSqlClusterFixture.OwnerARole,
                MaxCurrentPoints: 8,
                RetainedCurrentChanges: 32,
                IngressCapacity: 4,
                MaxProtocolSources: 1,
                PollInterval: TimeSpan.FromMilliseconds(50),
                PollTimeout: TimeSpan.FromSeconds(1),
                SchedulerMaxBindings: 1,
                SchedulerMaxInFlight: 1,
                ReconciliationInitialBackoff: TimeSpan.FromMilliseconds(20),
                ReconciliationMaxBackoff: TimeSpan.FromMilliseconds(100))
            {
                ConfigurationDatabaseRole = PostgreSqlClusterFixture.OwnerARole,
                DeploymentLeaseDuration = TimeSpan.FromSeconds(5),
                ConfigurationReconciliationInterval = TimeSpan.FromMilliseconds(50),
                Downstream = new RuntimeDownstreamOptions(
                    PostgreSqlClusterFixture.OwnerARole,
                    PostgreSqlClusterFixture.OwnerARole,
                    PostgreSqlClusterFixture.OwnerARole,
                    initialRevision.Value,
                    RevisionNumber.Initial,
                    historyMaxPageSize: 100,
                    historyMaxAggregateBuckets: 100,
                    eventMaxPageSize: 100,
                    eventRetainedProjectionChanges: 100,
                    eventMaxFeedChanges: 100,
                    new RuntimeDownstreamRetryPolicy(
                        maxAttempts: 2,
                        initialBackoff: TimeSpan.FromMilliseconds(10),
                        maximumBackoff: TimeSpan.FromMilliseconds(20))),
            };

        public async Task<CoreRuntimeCheckpoint> WaitForCurrentAsync(
            long expectedValue,
            ulong minimumBindingGeneration,
            TimeSpan timeout)
        {
            var store = new CoreRuntimeStore(
                DataSource,
                PostgreSqlClusterFixture.OwnerARole,
                Clock);
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var recovery = await store.LoadRecoveryAsync(
                    RuntimeScopeId.From(ScopeValue),
                    cancellation.Token);
                if (recovery.Checkpoint is { } checkpoint &&
                    checkpoint.Current.Count == 1 &&
                    checkpoint.Current[0].Value.Value == expectedValue &&
                    checkpoint.Current[0].BindingGeneration.Value >= minimumBindingGeneration)
                {
                    return checkpoint;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellation.Token);
            }
        }

        public async Task<CurrentEntry> WaitForPointAsync(
            Guid pointId,
            Func<CurrentEntry, bool> predicate,
            TimeSpan timeout)
        {
            var store = new CoreRuntimeStore(
                DataSource,
                PostgreSqlClusterFixture.OwnerARole,
                Clock);
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var recovery = await store.LoadRecoveryAsync(
                    RuntimeScopeId.From(ScopeValue),
                    cancellation.Token);
                var point = recovery.Checkpoint?.Current.FirstOrDefault(
                    item => item.PointId.Value == pointId);
                if (point is not null && predicate(point))
                {
                    return point;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellation.Token);
            }
        }

        public async Task WaitForModbusPipelineAsync(
            Guid pointId,
            TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var history = await CountPointRowsAsync(
                    $"{HistoryMigrations.Schema}.sample",
                    pointId,
                    cancellation.Token);
                var alarms = await CountPointRowsAsync(
                    $"{AlarmMigrations.Schema}.occurrence",
                    pointId,
                    cancellation.Token);
                var events = await CountPointRowsAsync(
                    $"{EventMigrations.Schema}.journal_event",
                    pointId,
                    cancellation.Token);
                if (history >= 3 && alarms >= 1 && events >= 1)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellation.Token);
            }
        }

        private async Task<long> CountPointRowsAsync(
            string table,
            Guid pointId,
            CancellationToken cancellationToken)
        {
            await using var command = DataSource.CreateCommand(
                $"SELECT count(*) FROM {table} WHERE point_id = @point_id;");
            command.Parameters.AddWithValue("point_id", pointId);
            return Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }

        public async Task ReplaceReleaseFingerprintAsync(
            ConfigurationRevisionId revisionId,
            string fingerprint)
        {
            await using var command = DataSource.CreateCommand(
                $"""
                UPDATE {ConfigurationMigrations.Schema}.distribution_job
                SET revision_fingerprint = @fingerprint
                WHERE revision_id = @revision_id;
                """);
            command.Parameters.AddWithValue("fingerprint", fingerprint);
            command.Parameters.AddWithValue("revision_id", revisionId.Value);
            await command.ExecuteNonQueryAsync();
        }

        public Task<long> CountSimulatorActionsAsync(string action) =>
            ScalarAsync(
                $"SELECT count(*) FROM {SimulatorRuntimeMigrations.Schema}.activation_audit WHERE action = @value;",
                action);

        public Task<long> CountWorkloadActionsAsync(string action) =>
            ScalarAsync(
                $"SELECT count(*) FROM {ConfigurationMigrations.Schema}.workload_deployment_event WHERE action = @value;",
                action);

        public Task<long> ReadAlarmEpochAsync() =>
            ScalarAsync(
                $"SELECT active_epoch FROM {AlarmMigrations.Schema}.scope_state WHERE scope_id = @value;",
                ScopeValue);

        private async Task<long> ScalarAsync(string sql, object value)
        {
            await using var command = DataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("value", value);
            return Convert.ToInt64(
                await command.ExecuteScalarAsync(),
                CultureInfo.InvariantCulture);
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            await Database.DisposeAsync();
        }

        public static string CreateModbusManifest(int port) =>
            JsonSerializer.Serialize(new
            {
                simulator = new
                {
                    sourceId = SourceValue,
                    seed = 17UL,
                    points = new[]
                    {
                        new
                        {
                            pointId = PointValue,
                            baseline = 1L,
                            amplitude = 0L,
                            unit = "kW",
                        },
                    },
                },
                protocolSources = new[]
                {
                    new
                    {
                        kind = "modbus_tcp_read_only",
                        sourceId = ModbusSourceValue,
                        host = "127.0.0.1",
                        port,
                        unitId = 7,
                        retry = new
                        {
                            maxAttempts = 1,
                            delayMs = 0,
                        },
                        points = new[]
                        {
                            new
                            {
                                pointId = ModbusPointValue,
                                table = "holding",
                                address = 10,
                                type = "unsigned16",
                                byteOrder = "big",
                                wordOrder = "high_first",
                                scale = 2m,
                                unit = "kW",
                            },
                        },
                    },
                },
                alarmDefinitions = new[]
                {
                    new
                    {
                        definitionId = ModbusAlarmValue,
                        pointId = ModbusPointValue,
                        name = "Modbus high value",
                        direction = "high",
                        threshold = 40L,
                        hysteresis = 0L,
                        raiseDelayMs = 0L,
                        clearDelayMs = 0L,
                        enabled = true,
                        priority = "high",
                    },
                },
            });

        private static string CreateManifest(long baseline) =>
            JsonSerializer.Serialize(new
            {
                simulator = new
                {
                    sourceId = SourceValue,
                    seed = 17UL,
                    points = new[]
                    {
                        new
                        {
                            pointId = PointValue,
                            baseline,
                            amplitude = 0L,
                            unit = "kW",
                        },
                    },
                },
                alarmDefinitions = new[]
                {
                    new
                    {
                        definitionId = AlarmValue,
                        pointId = PointValue,
                        name = "High value",
                        direction = "high",
                        threshold = 100L,
                        hysteresis = 0L,
                        raiseDelayMs = 0L,
                        clearDelayMs = 0L,
                        enabled = true,
                        priority = "high",
                    },
                },
            });
    }

    private sealed class MutableClock : IWallClock, IMonotonicClock
    {
        private DateTimeOffset now;

        public MutableClock(DateTimeOffset now) => this.now = now;

        public DateTimeOffset GetUtcNow() => now;

        public MonotonicTimestamp GetTimestamp() => new(now.UtcTicks);

        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) =>
            TimeSpan.FromTicks(finish.Value - start.Value);

        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
