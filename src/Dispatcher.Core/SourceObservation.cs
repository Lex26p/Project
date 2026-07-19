using Dispatcher.Semantics;

namespace Dispatcher.Core;

public sealed record SourceObservation
{
    public SourceObservation(
        RuntimeScopeId scopeId,
        SourceId sourceId,
        PointId pointId,
        OwnerPosition<SourceObservation> sourcePosition,
        TypedValue<long> value,
        Unit unit,
        DataQuality quality,
        Freshness freshness,
        SourceTimestamp sourceTimestamp)
    {
        _ = scopeId.Value;
        _ = sourceId.Value;
        _ = pointId.Value;
        if (sourcePosition.Value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourcePosition), "Source positions start at one.");
        }

        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(unit);
        ScopeId = scopeId;
        SourceId = sourceId;
        PointId = pointId;
        SourcePosition = sourcePosition;
        Value = value;
        Unit = unit;
        Quality = quality;
        Freshness = freshness;
        SourceTimestamp = sourceTimestamp;
    }

    public RuntimeScopeId ScopeId { get; }

    public SourceId SourceId { get; }

    public PointId PointId { get; }

    public OwnerPosition<SourceObservation> SourcePosition { get; }

    public TypedValue<long> Value { get; }

    public Unit Unit { get; }

    public DataQuality Quality { get; }

    public Freshness Freshness { get; }

    public SourceTimestamp SourceTimestamp { get; }
}
