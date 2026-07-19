namespace Dispatcher.Web;

public sealed class HistoryTrendState
{
    public IReadOnlyList<HistoryBucketPayload> Buckets { get; private set; } = [];

    public RuntimePointPayload? LivePoint { get; private set; }

    public bool RequiresHistoryRequery { get; private set; } = true;

    public void ApplyHistory(HistoryAggregatePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Buckets = payload.Buckets;
        RequiresHistoryRequery = false;
    }

    public void ApplyLiveSnapshot(RuntimeSnapshotPayload snapshot, Guid pointId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        LivePoint = snapshot.Points.SingleOrDefault(point => point.PointId == pointId);
    }

    public void MarkRealtimeGap()
    {
        LivePoint = null;
        RequiresHistoryRequery = true;
    }
}
