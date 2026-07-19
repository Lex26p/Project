using Dispatcher.Core;
using Dispatcher.Semantics;

namespace Dispatcher.Alarm;

public sealed class AlarmEvaluator
{
    private readonly RuntimeScopeId scopeId;
    private readonly RevisionNumber definitionEpoch;
    private readonly AlarmStore store;

    public AlarmEvaluator(RuntimeScopeId scopeId, RevisionNumber definitionEpoch, AlarmStore store)
    {
        _ = scopeId.Value;
        if (!definitionEpoch.IsDefined)
        {
            throw new ArgumentOutOfRangeException(nameof(definitionEpoch));
        }

        ArgumentNullException.ThrowIfNull(store);
        this.scopeId = scopeId;
        this.definitionEpoch = definitionEpoch;
        this.store = store;
    }

    public Task<Result<AlarmEvaluationSnapshot>> EvaluatePostRuntimeCutAsync(
        RuntimeCutAcceptance acceptedCut,
        CurrentSnapshot postCutSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acceptedCut);
        ArgumentNullException.ThrowIfNull(postCutSnapshot);
        if (acceptedCut.Cut.Binding.ScopeId != scopeId ||
            acceptedCut.Liveness.ScopeId != scopeId ||
            postCutSnapshot.ScopeId != scopeId)
        {
            return Task.FromResult(Failure("alarm.scope_mismatch", "Alarm evaluation inputs belong to another scope."));
        }

        if (acceptedCut.Liveness.LivenessPosition.Value == 0 ||
            acceptedCut.Liveness.SourceId != acceptedCut.Cut.Binding.SourceId ||
            acceptedCut.CurrentTransitions.Any(transition =>
                !postCutSnapshot.Entries.Any(entry =>
                    entry.PointId == transition.PointId &&
                    entry.CurrentPosition == transition.CurrentPosition)))
        {
            return Task.FromResult(Failure(
                "alarm.post_cut_required",
                "Alarm evaluation requires the accepted RuntimeCut and its post-cut current snapshot."));
        }

        var evaluatedAt = acceptedCut.Liveness.ProcessedTimestamp.Value;
        if (evaluatedAt.Offset != TimeSpan.Zero)
        {
            return Task.FromResult(Failure("alarm.timestamp", "Alarm evaluation timestamps must be UTC."));
        }

        return store.EvaluateAsync(
            scopeId,
            definitionEpoch,
            acceptedCut.Liveness.LivenessPosition,
            postCutSnapshot.Entries,
            evaluatedAt,
            cancellationToken);
    }

    private static Result<AlarmEvaluationSnapshot> Failure(string code, string message) =>
        Result.Failure<AlarmEvaluationSnapshot>(new OperationError(ErrorCode.From(code), message));
}
