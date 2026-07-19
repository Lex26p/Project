using Dispatcher.Semantics;

namespace Dispatcher.Core;

public readonly record struct SourceBindingGeneration : IComparable<SourceBindingGeneration>
{
    private SourceBindingGeneration(ulong value)
    {
        Value = value;
    }

    public ulong Value { get; }

    public static SourceBindingGeneration From(ulong value) => value == 0
        ? throw new ArgumentOutOfRangeException(nameof(value), "A source binding generation starts at one.")
        : new SourceBindingGeneration(value);

    public int CompareTo(SourceBindingGeneration other) => Value.CompareTo(other.Value);

    public static bool operator <(SourceBindingGeneration left, SourceBindingGeneration right) =>
        left.CompareTo(right) < 0;

    public static bool operator <=(SourceBindingGeneration left, SourceBindingGeneration right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >(SourceBindingGeneration left, SourceBindingGeneration right) =>
        left.CompareTo(right) > 0;

    public static bool operator >=(SourceBindingGeneration left, SourceBindingGeneration right) =>
        left.CompareTo(right) >= 0;
}

public readonly record struct SourceSessionGeneration : IComparable<SourceSessionGeneration>
{
    private SourceSessionGeneration(ulong value)
    {
        Value = value;
    }

    public ulong Value { get; }

    public static SourceSessionGeneration From(ulong value) => value == 0
        ? throw new ArgumentOutOfRangeException(nameof(value), "A source session generation starts at one.")
        : new SourceSessionGeneration(value);

    public int CompareTo(SourceSessionGeneration other) => Value.CompareTo(other.Value);

    public static bool operator <(SourceSessionGeneration left, SourceSessionGeneration right) =>
        left.CompareTo(right) < 0;

    public static bool operator <=(SourceSessionGeneration left, SourceSessionGeneration right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >(SourceSessionGeneration left, SourceSessionGeneration right) =>
        left.CompareTo(right) > 0;

    public static bool operator >=(SourceSessionGeneration left, SourceSessionGeneration right) =>
        left.CompareTo(right) >= 0;
}

public sealed record SourceBinding
{
    public SourceBinding(
        RuntimeScopeId scopeId,
        SourceId sourceId,
        SourceBindingGeneration bindingGeneration,
        SourceSessionGeneration sessionGeneration)
    {
        _ = scopeId.Value;
        _ = sourceId.Value;
        if (bindingGeneration.Value == 0)
        {
            throw new ArgumentException("A binding generation must be defined.", nameof(bindingGeneration));
        }

        if (sessionGeneration.Value == 0)
        {
            throw new ArgumentException("A session generation must be defined.", nameof(sessionGeneration));
        }

        ScopeId = scopeId;
        SourceId = sourceId;
        BindingGeneration = bindingGeneration;
        SessionGeneration = sessionGeneration;
    }

    public RuntimeScopeId ScopeId { get; }

    public SourceId SourceId { get; }

    public SourceBindingGeneration BindingGeneration { get; }

    public SourceSessionGeneration SessionGeneration { get; }

    internal bool IsNewerThan(SourceBinding other) =>
        BindingGeneration > other.BindingGeneration ||
        (BindingGeneration == other.BindingGeneration && SessionGeneration > other.SessionGeneration);
}
