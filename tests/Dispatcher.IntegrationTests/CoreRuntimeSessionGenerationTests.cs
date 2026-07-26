using Dispatcher.Core;
using Dispatcher.Persistence;
using Dispatcher.Semantics;
using Npgsql;
using Xunit;

namespace Dispatcher.IntegrationTests;

[Collection(PostgreSqlTestGroup.Name)]
public sealed class CoreRuntimeSessionGenerationTests
{
    private readonly PostgreSqlClusterFixture cluster;

    public CoreRuntimeSessionGenerationTests(PostgreSqlClusterFixture cluster)
    {
        this.cluster = cluster;
    }

    [Fact]
    public async Task AllocationPersistsAcrossStoreInstancesAndSeparatesKeys()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            CoreRuntimeMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));

        var firstScope = RuntimeScopeId.From(Guid.Parse("a1000000-0000-0000-0000-000000000001"));
        var secondScope = RuntimeScopeId.From(Guid.Parse("a1000000-0000-0000-0000-000000000002"));
        var firstSource = SourceId.From(Guid.Parse("a2000000-0000-0000-0000-000000000001"));
        var secondSource = SourceId.From(Guid.Parse("a2000000-0000-0000-0000-000000000002"));
        var firstStore = new CoreRuntimeStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, SystemClock.Instance);

        Assert.Equal(
            (ulong)1,
            (await firstStore.AllocateSourceSessionGenerationAsync(firstScope, firstSource)).Value);

        var restartedStore = new CoreRuntimeStore(
            dataSource,
            PostgreSqlClusterFixture.OwnerBRole,
            SystemClock.Instance);
        Assert.Equal(
            (ulong)2,
            (await restartedStore.AllocateSourceSessionGenerationAsync(firstScope, firstSource)).Value);
        Assert.Equal(
            (ulong)1,
            (await restartedStore.AllocateSourceSessionGenerationAsync(firstScope, secondSource)).Value);
        Assert.Equal(
            (ulong)1,
            (await restartedStore.AllocateSourceSessionGenerationAsync(secondScope, firstSource)).Value);
    }

    [Fact]
    public async Task ConcurrentAllocationsAreUniqueAndContiguous()
    {
        await using var database = await cluster.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await PostgresMigrationRunner.ApplyAsync(
            dataSource,
            CoreRuntimeMigrations.CreatePlan(PostgreSqlClusterFixture.OwnerBRole));

        var scopeId = RuntimeScopeId.From(Guid.Parse("a3000000-0000-0000-0000-000000000001"));
        var sourceId = SourceId.From(Guid.Parse("a4000000-0000-0000-0000-000000000001"));
        var store = new CoreRuntimeStore(dataSource, PostgreSqlClusterFixture.OwnerBRole, SystemClock.Instance);
        var allocations = Enumerable.Range(0, 16)
            .Select(_ => store.AllocateSourceSessionGenerationAsync(scopeId, sourceId))
            .ToArray();

        var generations = (await Task.WhenAll(allocations))
            .Select(item => item.Value)
            .OrderBy(item => item)
            .ToArray();

        Assert.Equal(Enumerable.Range(1, 16).Select(item => checked((ulong)item)), generations);
    }
}
