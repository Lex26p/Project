using Dispatcher.Core;
using Dispatcher.Semantics;

namespace Dispatcher.Simulator;

public sealed record SimulatorPointConfig
{
    public SimulatorPointConfig(PointId pointId, long baseline, long amplitude, Unit unit)
    {
        _ = pointId.Value;
        ArgumentOutOfRangeException.ThrowIfNegative(amplitude);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(amplitude, (long.MaxValue - 1) / 2);

        ArgumentNullException.ThrowIfNull(unit);
        PointId = pointId;
        Baseline = baseline;
        Amplitude = amplitude;
        Unit = unit;
    }

    public PointId PointId { get; }

    public long Baseline { get; }

    public long Amplitude { get; }

    public Unit Unit { get; }
}

public sealed class SimulatorScenarioConfig
{
    public SimulatorScenarioConfig(
        RuntimeScopeId scopeId,
        SourceId sourceId,
        ulong seed,
        IEnumerable<SimulatorPointConfig> points)
    {
        _ = scopeId.Value;
        _ = sourceId.Value;
        ArgumentNullException.ThrowIfNull(points);
        var orderedPoints = points.OrderBy(point => point.PointId.Value).ToArray();
        if (orderedPoints.Length == 0)
        {
            throw new ArgumentException("A Simulator scenario requires at least one point.", nameof(points));
        }

        if (orderedPoints.Select(point => point.PointId).Distinct().Count() != orderedPoints.Length)
        {
            throw new ArgumentException("Simulator point identities must be unique.", nameof(points));
        }

        ScopeId = scopeId;
        SourceId = sourceId;
        Seed = seed;
        Points = orderedPoints;
    }

    public RuntimeScopeId ScopeId { get; }

    public SourceId SourceId { get; }

    public ulong Seed { get; }

    public IReadOnlyList<SimulatorPointConfig> Points { get; }
}
