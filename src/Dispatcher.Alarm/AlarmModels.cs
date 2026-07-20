using Dispatcher.Core;
using Dispatcher.Semantics;

namespace Dispatcher.Alarm;

public readonly record struct AlarmDefinitionId
{
    private readonly CanonicalId<AlarmDefinitionId> value;

    private AlarmDefinitionId(CanonicalId<AlarmDefinitionId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static AlarmDefinitionId New() => new(CanonicalId.New<AlarmDefinitionId>());

    public static AlarmDefinitionId From(Guid value) => new(CanonicalId.From<AlarmDefinitionId>(value));
}

public readonly record struct AlarmOccurrenceId
{
    private readonly CanonicalId<AlarmOccurrenceId> value;

    private AlarmOccurrenceId(CanonicalId<AlarmOccurrenceId> value)
    {
        this.value = value;
    }

    public Guid Value => value.Value;

    public static AlarmOccurrenceId New() => new(CanonicalId.New<AlarmOccurrenceId>());

    public static AlarmOccurrenceId From(Guid value) => new(CanonicalId.From<AlarmOccurrenceId>(value));
}

public enum AlarmThresholdDirection
{
    High = 1,
    Low = 2,
}

public enum AlarmConditionState
{
    Normal = 1,
    PendingRaise = 2,
    Active = 3,
    PendingClear = 4,
}

public enum AlarmAcknowledgementState
{
    Unacknowledged = 1,
    Acknowledged = 2,
}

public enum AlarmPriority
{
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}

public sealed record AlarmDefinition
{
    public AlarmDefinition(
        AlarmDefinitionId definitionId,
        PointId pointId,
        string name,
        AlarmThresholdDirection direction,
        long threshold,
        long hysteresis,
        TimeSpan raiseDelay,
        TimeSpan clearDelay,
        bool enabled = true,
        AlarmPriority priority = AlarmPriority.Medium)
    {
        _ = definitionId.Value;
        _ = pointId.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(hysteresis);
        ArgumentOutOfRangeException.ThrowIfLessThan(raiseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(clearDelay, TimeSpan.Zero);
        if (name.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(name), "Alarm definition names are limited to 200 characters.");
        }

        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }

        DefinitionId = definitionId;
        PointId = pointId;
        Name = name.Trim();
        Direction = direction;
        Threshold = threshold;
        Hysteresis = hysteresis;
        RaiseDelay = raiseDelay;
        ClearDelay = clearDelay;
        Enabled = enabled;
        Priority = priority;
    }

    public AlarmDefinitionId DefinitionId { get; }

    public PointId PointId { get; }

    public string Name { get; }

    public AlarmThresholdDirection Direction { get; }

    public long Threshold { get; }

    public long Hysteresis { get; }

    public TimeSpan RaiseDelay { get; }

    public TimeSpan ClearDelay { get; }

    public bool Enabled { get; }

    public AlarmPriority Priority { get; }
}

public sealed record AlarmDefinitionSet
{
    public AlarmDefinitionSet(
        RuntimeScopeId scopeId,
        RevisionNumber epoch,
        IReadOnlyCollection<AlarmDefinition> definitions)
    {
        _ = scopeId.Value;
        if (!epoch.IsDefined)
        {
            throw new ArgumentOutOfRangeException(nameof(epoch));
        }

        ArgumentNullException.ThrowIfNull(definitions);
        var ordered = definitions.OrderBy(item => item.DefinitionId.Value).ToArray();
        if (ordered.Select(item => item.DefinitionId).Distinct().Count() != ordered.Length)
        {
            throw new ArgumentException("Alarm definition identities must be unique within an epoch.", nameof(definitions));
        }

        ScopeId = scopeId;
        Epoch = epoch;
        Definitions = ordered;
    }

    public RuntimeScopeId ScopeId { get; }

    public RevisionNumber Epoch { get; }

    public IReadOnlyList<AlarmDefinition> Definitions { get; }
}

public sealed record AlarmConditionFacet(
    AlarmConditionState State,
    DateTimeOffset? PendingSince,
    DateTimeOffset? ActiveSince,
    DateTimeOffset? ClearedAt,
    StateVersion Version);

public sealed record AlarmAcknowledgementFacet(
    AlarmAcknowledgementState State,
    Guid? AcknowledgedBy,
    DateTimeOffset? AcknowledgedAt,
    StateVersion Version);

public sealed record AlarmAssignmentFacet(
    Guid? AssignedTo,
    DateTimeOffset? AssignedAt,
    StateVersion Version);

public sealed record AlarmShelvingFacet(
    DateTimeOffset? ShelvedUntil,
    string? Reason,
    StateVersion Version);

public sealed record AlarmSuppressionFacet(
    bool IsSuppressed,
    string? Reason,
    StateVersion Version);

public sealed record AlarmOccurrenceSnapshot(
    AlarmOccurrenceId OccurrenceId,
    RuntimeScopeId ScopeId,
    RevisionNumber DefinitionEpoch,
    AlarmDefinitionId DefinitionId,
    PointId PointId,
    AlarmPriority Priority,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    AlarmConditionFacet Condition,
    AlarmAcknowledgementFacet Acknowledgement,
    AlarmAssignmentFacet Assignment,
    AlarmShelvingFacet Shelving,
    AlarmSuppressionFacet Suppression);

public sealed record AlarmEvaluationSnapshot(
    RuntimeScopeId ScopeId,
    RevisionNumber DefinitionEpoch,
    OwnerPosition<SourceLiveness> EvaluationPosition,
    IReadOnlyList<AlarmOccurrenceSnapshot> Occurrences);
