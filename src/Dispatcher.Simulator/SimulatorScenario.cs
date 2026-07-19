using Dispatcher.Core;
using Dispatcher.Semantics;

namespace Dispatcher.Simulator;

public sealed class SimulatorScenario
{
    private readonly SimulatorScenarioConfig config;
    private readonly IWallClock wallClock;
    private SplitMix64 random;
    private ulong sourcePosition;

    public SimulatorScenario(SimulatorScenarioConfig config, IWallClock wallClock)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(wallClock);
        this.config = config;
        this.wallClock = wallClock;
        random = new SplitMix64(config.Seed);
    }

    public IReadOnlyList<SourceObservation> NextStep()
    {
        var observations = new SourceObservation[config.Points.Count];
        for (var index = 0; index < config.Points.Count; index++)
        {
            var point = config.Points[index];
            var value = checked(point.Baseline + NextOffset(point.Amplitude));
            sourcePosition = checked(sourcePosition + 1);
            observations[index] = new SourceObservation(
                config.ScopeId,
                config.SourceId,
                point.PointId,
                new OwnerPosition<SourceObservation>(sourcePosition),
                TypedValue.From(value),
                point.Unit,
                DataQuality.Good,
                Freshness.Fresh,
                SourceTimestamp.FromUtc(wallClock.GetUtcNow()));
        }

        return observations;
    }

    private long NextOffset(long amplitude)
    {
        if (amplitude == 0)
        {
            return 0;
        }

        var range = checked((ulong)((amplitude * 2) + 1));
        return checked((long)(random.Next() % range) - amplitude);
    }

    private struct SplitMix64
    {
        private ulong state;

        public SplitMix64(ulong seed)
        {
            state = seed;
        }

        public ulong Next()
        {
            state = unchecked(state + 0x9E3779B97F4A7C15UL);
            var value = state;
            value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
            value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
            return value ^ (value >> 31);
        }
    }
}
