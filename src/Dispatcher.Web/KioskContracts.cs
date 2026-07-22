namespace Dispatcher.Web;

public sealed record KioskAssignedRuntimePayload(
    Guid TerminalId, Guid DeviceIdentityId, Guid ProfileId, ulong ProfileVersion,
    string Experience, string OfflineMode, bool EmployeeReauthenticationRequired,
    KioskDashboardPayload Dashboard, DateTimeOffset SynchronizedAt);
public sealed record KioskDashboardPayload(
    Guid DashboardId, Guid RevisionId, ulong RevisionNumber, string Name, string? Description,
    IReadOnlyList<KioskWindowPayload> Windows, IReadOnlyList<KioskDependencyPayload> Dependencies,
    DateTimeOffset PublishedAt);
public sealed record KioskWindowPayload(Guid WindowId, string Title,
    IReadOnlyList<KioskWidgetPayload> Widgets, IReadOnlyList<KioskBindingPayload> Bindings);
public sealed record KioskWidgetPayload(Guid WidgetId, string Kind, string Title, IReadOnlyList<Guid> BindingIds);
public sealed record KioskBindingPayload(Guid BindingId, string Source, Guid ScopeId, Guid PointId);
public sealed record KioskDependencyPayload(Guid BindingId, string Key, string Fingerprint);
public sealed record KioskHeartbeatRequest(ulong SynchronizedProfileVersion);
public sealed record KioskHeartbeatPayload(DateTimeOffset AcceptedAt, ulong ProfileVersion, bool ResyncRequired);

public sealed class KioskRuntimeState
{
    private int pendingCommandCount;
    public KioskAssignedRuntimePayload? Runtime { get; private set; }
    public bool Online { get; private set; }
    public int PendingCommandCount => pendingCommandCount;

    public void Synchronized(KioskAssignedRuntimePayload runtime)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Online = true;
    }

    public void Disconnected()
    {
        Online = false;
        if (!string.Equals(Runtime?.OfflineMode, "ReadOnlyLastSynchronized", StringComparison.Ordinal)) Runtime = null;
    }

    public bool TryQueueCommand()
    {
        pendingCommandCount = 0;
        return false;
    }
}
