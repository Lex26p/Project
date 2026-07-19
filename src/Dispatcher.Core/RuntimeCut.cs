using Dispatcher.Semantics;

namespace Dispatcher.Core;

public sealed class RuntimeCut
{
    private RuntimeCut(
        SourceBinding binding,
        ulong scheduleSequence,
        IReadOnlyList<SourceObservation> observations)
    {
        Binding = binding;
        ScheduleSequence = scheduleSequence;
        Observations = observations;
    }

    public SourceBinding Binding { get; }

    public ulong ScheduleSequence { get; }

    public IReadOnlyList<SourceObservation> Observations { get; }

    public static Result<RuntimeCut> Normalize(
        SourceBinding binding,
        ulong scheduleSequence,
        IEnumerable<SourceObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentOutOfRangeException.ThrowIfZero(scheduleSequence);

        var ordered = observations.OrderBy(observation => observation.PointId.Value).ToArray();
        if (ordered.Any(observation =>
                observation.ScopeId != binding.ScopeId || observation.SourceId != binding.SourceId))
        {
            return Failure("core.cut_binding", "Every normalized observation must belong to the cut source binding.");
        }

        if (ordered.Select(observation => observation.PointId).Distinct().Count() != ordered.Length)
        {
            return Failure("core.cut_duplicate_point", "A RuntimeCut cannot contain duplicate point identities.");
        }

        if (ordered.Select(observation => observation.SourcePosition.Value).Distinct().Count() != ordered.Length)
        {
            return Failure("core.cut_duplicate_position", "A RuntimeCut cannot contain duplicate source positions.");
        }

        return Result.Success(new RuntimeCut(binding, scheduleSequence, ordered));
    }

    private static Result<RuntimeCut> Failure(string code, string message) =>
        Result.Failure<RuntimeCut>(new OperationError(ErrorCode.From(code), message));
}
