using System.Diagnostics;
using Npgsql;

namespace Dispatcher.Platform;

public enum ReadinessState
{
    Ready = 1,
    NotReady = 2,
}

public sealed record PlatformHealthSnapshot(bool IsLive, ReadinessState Readiness, string? ReasonCode);

public static class PlatformHealth
{
    public static async Task<PlatformHealthSnapshot> CheckAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        using var activity = PlatformDiagnostics.ActivitySource.StartActivity("health.readiness");

        try
        {
            await using var command = dataSource.CreateCommand(
                "SELECT to_regclass('platform_nucleus.__dispatcher_migrations') IS NOT NULL;");
            var migrated = (bool?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) == true;
            return migrated
                ? new PlatformHealthSnapshot(true, ReadinessState.Ready, null)
                : new PlatformHealthSnapshot(true, ReadinessState.NotReady, "platform_schema_missing");
        }
        catch (NpgsqlException exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.SqlState);
            return new PlatformHealthSnapshot(true, ReadinessState.NotReady, "database_unavailable");
        }
    }
}
