namespace Dispatcher.Server;

public sealed record RuntimePointPayload(
    Guid PointId,
    long Value,
    string Unit,
    string Quality,
    string Freshness,
    DateTimeOffset? SourceTimestamp = null,
    DateTimeOffset? ReceiveTimestamp = null,
    DateTimeOffset? ProcessedTimestamp = null);

public sealed record RuntimeSnapshotPayload(
    Guid ScopeId,
    ulong Cursor,
    IReadOnlyList<RuntimePointPayload> Points);

public sealed record RuntimeDeltaPayload(
    Guid ScopeId,
    ulong From,
    ulong To,
    IReadOnlyList<RuntimePointPayload> Changes);

public sealed record RuntimeReadinessPayload(
    Guid ScopeId,
    bool Published,
    bool Ready,
    bool CanServeCurrent,
    string? DegradationReasonCode,
    DateTimeOffset? HeartbeatAt,
    DateTimeOffset? PublishedAt);

public enum RealtimePollKind
{
    Delta = 1,
    NoChange = 2,
    Gap = 3,
    PermissionInvalidated = 4,
}

public sealed record RealtimePollPayload(
    RealtimePollKind Kind,
    RuntimeDeltaPayload? Delta = null);
