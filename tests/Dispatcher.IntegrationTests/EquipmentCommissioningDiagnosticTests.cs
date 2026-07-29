using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Modbus;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.ProtocolCommissioning;
using Dispatcher.Protocols;
using Dispatcher.RuntimeHost;
using Dispatcher.Semantics;
using Dispatcher.Snmp;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class EquipmentCommissioningDiagnosticTests
{
    private readonly PostgreSqlClusterFixture cluster;

    public EquipmentCommissioningDiagnosticTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task RuntimeHostWorkerCompletesDurableSnmpJobAndFingerprintBecomesStale()
    {
        await using var peer = new FakeSnmpUdpPeer(
            (request, _, _) => ValueTask.FromResult<byte[]?>(
                FakeSnmpUdpPeer.Response(request, [(0x43, [42])])));
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            EquipmentMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));

        var now = DateTimeOffset.UtcNow;
        var clock = new SystemClock(TimeProvider.System);
        var scopeId = FacilityScopeId.From(Guid.Parse("c1400000-0000-0000-0000-000000000001"));
        var locationId = LocationId.From(Guid.Parse("c1400000-0000-0000-0000-000000000002"));
        var session = new SessionSnapshot(
            SessionId.New(),
            SubjectId.New(),
            PrincipalKind.User,
            now.AddMinutes(-1),
            now.AddHours(1),
            new EffectivePermissions(
            [
                EquipmentPermissions.Read(scopeId),
                EquipmentPermissions.Write(scopeId),
            ]));
        var access = SessionAuthorization.AuthorizeAccess(
            session, EquipmentPermissions.Write(scopeId), clock).Value;
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var protector = new StagingSecretProtector(key);
        var store = new EquipmentStagingStore(
            dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        var input = EquipmentStagingDraftInput.New(
            scopeId, locationId, EquipmentProtocol.Snmp) with
        {
            Code = "SW-C14",
            Name = "Commissioning switch",
            Host = "127.0.0.1",
            Port = peer.Port,
            Secret = WriteOnlySecret.From("c14-private"),
            Unit = "s",
        };
        var draft = await store.SaveDraftAsync(access, input, protector);
        Assert.True(draft.IsSuccess);
        Assert.True(draft.Value.HasSecret);
        Assert.DoesNotContain("c14-private", System.Text.Json.JsonSerializer.Serialize(draft.Value));

        var enqueued = await store.EnqueueDiagnosticAsync(
            access, scopeId, draft.Value.RowId, EquipmentDiagnosticMode.SamplePoll);
        Assert.Equal(EquipmentDiagnosticJobStatus.Pending, enqueued.Value.Status);
        var workload = ProtocolWorkloadIdentity.From("runtime-c14-diagnostic");
        var worker = new EquipmentDiagnosticWorker(
            store,
            scopeId,
            workload.Value,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            new ProtocolCommissioningLimits(
                new ModbusConfigurationLimits(8, 16),
                new SnmpConfigurationLimits(8, 32, 128)),
            new SnmpWireLimits(64, 1024),
            workload,
            new ProtocolIoLimits(TimeSpan.FromSeconds(1), 4096, 8, 1),
            new DatabaseProtocolSecretResolver(store, scopeId, protector, workload),
            new TcpModbusConnectionFactory(),
            new UdpSnmpDatagramClientFactory(),
            clock);
        using var cancellation = new CancellationTokenSource();
        var run = worker.RunAsync(cancellation.Token);
        var completed = await WaitForCompletionAsync(store, scopeId, enqueued.Value.JobId);
        cancellation.Cancel();
        Assert.True((await run).IsSuccess);

        Assert.True(
            completed.Status == EquipmentDiagnosticJobStatus.Succeeded,
            $"{completed.OutcomeCode}: {completed.OutcomeMessage}");
        Assert.False(completed.IsStale);
        Assert.Equal(42, Assert.Single(completed.Samples).Value);
        Assert.Equal([0xA0], peer.PduTypes);

        var changed = await store.SaveDraftAsync(
            access,
            input with
            {
                Name = "Changed after diagnostic",
                Secret = null,
                ExpectedVersion = draft.Value.Version,
            },
            protector);
        Assert.True(changed.IsSuccess);
        var stale = await store.ReadDiagnosticAsync(scopeId, completed.JobId);
        Assert.True(stale.Value.IsStale);

        var restartedStore = new EquipmentStagingStore(
            dataSource, PostgreSqlClusterFixture.OwnerBRole, clock);
        var durable = await restartedStore.ReadLatestDiagnosticAsync(scopeId, draft.Value.RowId);
        Assert.Equal(completed.JobId, durable.Value.JobId);
        Assert.DoesNotContain("c14-private", await ReadStoredTextAsync(dataSource));
    }

    private static async Task<EquipmentDiagnosticJobSnapshot> WaitForCompletionAsync(
        EquipmentStagingStore store,
        FacilityScopeId scopeId,
        Guid jobId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var job = (await store.ReadDiagnosticAsync(scopeId, jobId, timeout.Token)).Value;
            if (job.Status is not (EquipmentDiagnosticJobStatus.Pending or EquipmentDiagnosticJobStatus.Running))
            {
                return job;
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task<string> ReadStoredTextAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            SELECT coalesce(string_agg(value, ' '), '') FROM (
                SELECT encode(protected_value, 'hex') AS value
                FROM {EquipmentMigrations.Schema}.protocol_secret
                UNION ALL
                SELECT manifest::text || ' ' || coalesce(outcome_message, '') AS value
                FROM {EquipmentMigrations.Schema}.diagnostic_job
                UNION ALL
                SELECT action || ' ' || permission AS value
                FROM {EquipmentMigrations.Schema}.commissioning_audit
            ) values;
            """);
        return (string)(await command.ExecuteScalarAsync() ?? string.Empty);
    }
}
