using Dispatcher.Semantics;

namespace Dispatcher.Core;

public enum RuntimeFactProtection
{
    Rebuildable = 1,
    Protected = 2,
}

public enum RuntimeFactClass
{
    CurrentCheckpoint = 1,
    SourceCut = 2,
    SourceGap = 3,
}

public static class RuntimeFactClassifier
{
    public static RuntimeFactProtection Classify(RuntimeFactClass factClass) => factClass switch
    {
        RuntimeFactClass.CurrentCheckpoint => RuntimeFactProtection.Rebuildable,
        RuntimeFactClass.SourceCut or RuntimeFactClass.SourceGap => RuntimeFactProtection.Protected,
        _ => throw new ArgumentOutOfRangeException(nameof(factClass)),
    };
}

public sealed record RuntimeSourceGap
{
    public RuntimeSourceGap(
        SourceBinding binding,
        ulong scheduleSequence,
        ulong firstSourcePosition,
        ulong lastSourcePosition,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentOutOfRangeException.ThrowIfZero(scheduleSequence);
        ArgumentOutOfRangeException.ThrowIfZero(firstSourcePosition);
        ArgumentOutOfRangeException.ThrowIfLessThan(lastSourcePosition, firstSourcePosition);

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        Binding = binding;
        ScheduleSequence = scheduleSequence;
        FirstSourcePosition = firstSourcePosition;
        LastSourcePosition = lastSourcePosition;
        Reason = reason.Trim();
    }

    public SourceBinding Binding { get; }

    public ulong ScheduleSequence { get; }

    public ulong FirstSourcePosition { get; }

    public ulong LastSourcePosition { get; }

    public string Reason { get; }
}

public sealed record SourceRuntimeCheckpoint(
    SourceBinding Binding,
    ulong SourcePosition);

public sealed record CoreRuntimeCheckpoint(
    RuntimeScopeId ScopeId,
    OwnerPosition<CurrentEntry> CurrentPosition,
    OwnerPosition<SourceLiveness> LivenessPosition,
    IReadOnlyList<SourceRuntimeCheckpoint> Sources,
    IReadOnlyList<CurrentEntry> Current,
    IReadOnlyList<SourceLiveness> Liveness);

public sealed record RuntimeSourceObligation(
    RuntimeScopeId ScopeId,
    OwnerPosition<RuntimeSourceObligation> Position,
    RuntimeFactClass FactClass,
    RuntimeFactProtection Protection,
    SourceBinding Binding,
    ulong ScheduleSequence,
    RuntimeCut? Cut,
    RuntimeSourceGap? Gap,
    DateTimeOffset RecordedAt,
    DateTimeOffset? CheckpointedAt);

public sealed record RuntimeRecoveryState(
    CoreRuntimeCheckpoint? Checkpoint,
    ulong CheckpointObligationPosition,
    bool ProtectedContinuity,
    IReadOnlyList<RuntimeSourceObligation> PendingObligations);
