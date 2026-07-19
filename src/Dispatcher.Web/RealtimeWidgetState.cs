namespace Dispatcher.Web;

public sealed class RealtimeWidgetState
{
    private readonly Dictionary<Guid, RuntimePointPayload> points = [];

    public Guid ScopeId { get; private set; }

    public ulong Cursor { get; private set; }

    public bool NeedsResync { get; private set; } = true;

    public bool PermissionInvalidated { get; private set; }

    public bool HasPendingRender { get; private set; }

    public IReadOnlyList<RuntimePointPayload> Points => points.Values
        .OrderBy(point => point.PointId)
        .ToArray();

    public void ApplySnapshot(RuntimeSnapshotPayload snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        points.Clear();
        foreach (var point in snapshot.Points)
        {
            points[point.PointId] = point;
        }

        ScopeId = snapshot.ScopeId;
        Cursor = snapshot.Cursor;
        NeedsResync = false;
        PermissionInvalidated = false;
        HasPendingRender = true;
    }

    public void ApplyPoll(RealtimePollPayload poll)
    {
        ArgumentNullException.ThrowIfNull(poll);
        switch (poll.Kind)
        {
            case RealtimePollKind.NoChange:
                return;
            case RealtimePollKind.Gap:
                NeedsResync = true;
                return;
            case RealtimePollKind.PermissionInvalidated:
                points.Clear();
                PermissionInvalidated = true;
                NeedsResync = true;
                HasPendingRender = true;
                return;
            case RealtimePollKind.Delta:
                ApplyDelta(poll.Delta ?? throw new InvalidOperationException("Delta payload is missing."));
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(poll));
        }
    }

    public void MarkDisconnected() => NeedsResync = true;

    public bool ConsumeRenderRequest()
    {
        var result = HasPendingRender;
        HasPendingRender = false;
        return result;
    }

    private void ApplyDelta(RuntimeDeltaPayload delta)
    {
        if (delta.ScopeId != ScopeId || delta.From != Cursor)
        {
            NeedsResync = true;
            return;
        }

        foreach (var point in delta.Changes)
        {
            points[point.PointId] = point;
        }

        Cursor = delta.To;
        HasPendingRender = delta.Changes.Count > 0 || HasPendingRender;
    }
}
