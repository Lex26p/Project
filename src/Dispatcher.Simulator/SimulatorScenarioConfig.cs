using Dispatcher.Core;
using Dispatcher.Semantics;

namespace Dispatcher.Simulator;

public sealed record SimulatorPointConfig
{
    public SimulatorPointConfig(PointId pointId, decimal baseline, decimal amplitude, Unit unit)
    {
        _ = pointId.Value;
        ArgumentOutOfRangeException.ThrowIfNegative(amplitude);
        if (!MeasurementValue.IsRepresentable(baseline) ||
            !MeasurementValue.IsRepresentable(amplitude) ||
            !MeasurementValue.IsRepresentable(baseline - amplitude) ||
            !MeasurementValue.IsRepresentable(baseline + amplitude))
        {
            throw new ArgumentOutOfRangeException(
                nameof(amplitude),
                "Simulator values must fit the decimal measurement envelope.");
        }

        ArgumentNullException.ThrowIfNull(unit);
        PointId = pointId;
        Baseline = baseline;
        Amplitude = amplitude;
        Unit = unit;
    }

    public PointId PointId { get; }

    public decimal Baseline { get; }

    public decimal Amplitude { get; }

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
