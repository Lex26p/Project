using System.Text;
using Dispatcher.Persistence;
using Dispatcher.Platform;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class PlatformNucleusTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 1, 0, 0, TimeSpan.Zero);
    private readonly PostgreSqlClusterFixture cluster;

    public PlatformNucleusTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task AdmissionIsIdempotentAndCreatesOneAuditRecord()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            PlatformMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        var permission = PermissionCode.From("equipment.write");
        var authorization = Authorize(permission);
        var store = new OperationAdmissionStore(
            dataSource,
            PostgreSqlClusterFixture.OwnerARole,
            new FixedClock(Now));
        var fingerprint = RequestFingerprint.Compute(Encoding.UTF8.GetBytes("request-a"));
        var first = new OperationAdmissionRequest(OperationId.New(), "key-1", fingerprint, "equipment.update");
        var replay = new OperationAdmissionRequest(OperationId.New(), "key-1", fingerprint, "equipment.update");
        var conflict = new OperationAdmissionRequest(
            OperationId.New(),
            "key-1",
            RequestFingerprint.Compute(Encoding.UTF8.GetBytes("request-b")),
            "equipment.update");

        var acceptedResult = await store.AdmitAsync(authorization, first);
        var replayResult = await store.AdmitAsync(authorization, replay);
        var conflictResult = await store.AdmitAsync(authorization, conflict);

        Assert.Equal(AdmissionDisposition.Accepted, acceptedResult.Disposition);
        Assert.Equal(AdmissionDisposition.Replay, replayResult.Disposition);
        Assert.Equal(first.OperationId, replayResult.OperationId);
        Assert.Equal(AdmissionDisposition.Conflict, conflictResult.Disposition);
        Assert.Equal(1L, await CountAsync(dataSource, "operation_admission"));
        Assert.Equal(1L, await CountAsync(dataSource, "audit_admission"));
    }

    [Fact]
    public async Task HealthAndDurableJobsSupportRestartSafeLeases()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var beforeMigration = await PlatformHealth.CheckAsync(dataSource);
        Assert.Equal(ReadinessState.NotReady, beforeMigration.Readiness);

        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            PlatformMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerARole));
        var afterMigration = await PlatformHealth.CheckAsync(dataSource);
        Assert.True(afterMigration.IsLive);
        Assert.Equal(ReadinessState.Ready, afterMigration.Readiness);

        var store = new DurableJobStore(dataSource, PostgreSqlClusterFixture.OwnerARole);
        var jobId = JobId.New();
        await store.EnqueueAsync(jobId, "audit.dispatch", "{\"operation\":1}", Now);

        var firstClaim = await store.ClaimNextAsync("worker-a", Now, TimeSpan.FromSeconds(30));
        var whileLeased = await store.ClaimNextAsync("worker-b", Now.AddSeconds(10), TimeSpan.FromSeconds(30));
        var wrongWorkerCompletion = await store.CompleteAsync(jobId, "worker-b", Now.AddSeconds(10));
        var reclaimed = await store.ClaimNextAsync("worker-b", Now.AddSeconds(31), TimeSpan.FromSeconds(30));
        var completed = await store.CompleteAsync(jobId, "worker-b", Now.AddSeconds(32));
        var afterCompletion = await store.ClaimNextAsync("worker-c", Now.AddMinutes(2), TimeSpan.FromSeconds(30));

        Assert.Equal(jobId, firstClaim?.Id);
        Assert.Equal(1, firstClaim?.Attempts);
        Assert.Null(whileLeased);
        Assert.False(wrongWorkerCompletion);
        Assert.Equal(jobId, reclaimed?.Id);
        Assert.Equal(2, reclaimed?.Attempts);
        Assert.True(completed);
        Assert.Null(afterCompletion);
    }

    private static AuthorizedMutation Authorize(PermissionCode permission)
    {
        var session = new SessionSnapshot(
            SessionId.New(),
            SubjectId.New(),
            PrincipalKind.User,
            Now.AddMinutes(-1),
            Now.AddHours(1),
            new EffectivePermissions([permission]));
        return SessionAuthorization.AuthorizeMutation(session, permission, new FixedClock(Now)).Value;
    }

    private static async Task<long> CountAsync(NpgsqlDataSource dataSource, string table)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT count(*) FROM {PlatformMigrations.Schema}.{table};");
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private sealed class FixedClock : IWallClock
    {
        private readonly DateTimeOffset now;

        public FixedClock(DateTimeOffset now)
        {
            this.now = now;
        }

        public DateTimeOffset GetUtcNow() => now;
    }
}
