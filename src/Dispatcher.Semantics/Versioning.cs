namespace Dispatcher.Semantics;

public readonly record struct RevisionNumber : IComparable<RevisionNumber>
{
    private RevisionNumber(ulong value)
    {
        Value = value;
    }

    public ulong Value { get; }

    public bool IsDefined => Value != 0;

    public static RevisionNumber Initial => new(1);

    public static RevisionNumber From(ulong value) => value == 0
        ? throw new ArgumentOutOfRangeException(nameof(value), "A revision starts at one.")
        : new RevisionNumber(value);

    public RevisionNumber Next() => new(checked(Value + 1));

    public int CompareTo(RevisionNumber other) => Value.CompareTo(other.Value);

    public static bool operator <(RevisionNumber left, RevisionNumber right) => left.CompareTo(right) < 0;

    public static bool operator <=(RevisionNumber left, RevisionNumber right) => left.CompareTo(right) <= 0;

    public static bool operator >(RevisionNumber left, RevisionNumber right) => left.CompareTo(right) > 0;

    public static bool operator >=(RevisionNumber left, RevisionNumber right) => left.CompareTo(right) >= 0;
}

public readonly record struct StateVersion : IComparable<StateVersion>
{
    private StateVersion(ulong value)
    {
        Value = value;
    }

    public ulong Value { get; }

    public bool IsDefined => Value != 0;

    public static StateVersion Initial => new(1);

    public static StateVersion From(ulong value) => value == 0
        ? throw new ArgumentOutOfRangeException(nameof(value), "A version starts at one.")
        : new StateVersion(value);

    public StateVersion Next() => new(checked(Value + 1));

    public int CompareTo(StateVersion other) => Value.CompareTo(other.Value);

    public static bool operator <(StateVersion left, StateVersion right) => left.CompareTo(right) < 0;

    public static bool operator <=(StateVersion left, StateVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >(StateVersion left, StateVersion right) => left.CompareTo(right) > 0;

    public static bool operator >=(StateVersion left, StateVersion right) => left.CompareTo(right) >= 0;
}
