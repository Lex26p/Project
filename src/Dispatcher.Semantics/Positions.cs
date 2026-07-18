namespace Dispatcher.Semantics;

public readonly record struct OwnerPosition<TScope>(ulong Value) : IComparable<OwnerPosition<TScope>>
{
    public OwnerPosition<TScope> Next() => new(checked(Value + 1));

    public int CompareTo(OwnerPosition<TScope> other) => Value.CompareTo(other.Value);

    public static bool operator <(OwnerPosition<TScope> left, OwnerPosition<TScope> right) => left.CompareTo(right) < 0;

    public static bool operator <=(OwnerPosition<TScope> left, OwnerPosition<TScope> right) => left.CompareTo(right) <= 0;

    public static bool operator >(OwnerPosition<TScope> left, OwnerPosition<TScope> right) => left.CompareTo(right) > 0;

    public static bool operator >=(OwnerPosition<TScope> left, OwnerPosition<TScope> right) => left.CompareTo(right) >= 0;
}

public readonly record struct ConsumerCursor<TScope>(ulong Value) : IComparable<ConsumerCursor<TScope>>
{
    public ConsumerCursor<TScope> Next() => new(checked(Value + 1));

    public int CompareTo(ConsumerCursor<TScope> other) => Value.CompareTo(other.Value);

    public static bool operator <(ConsumerCursor<TScope> left, ConsumerCursor<TScope> right) => left.CompareTo(right) < 0;

    public static bool operator <=(ConsumerCursor<TScope> left, ConsumerCursor<TScope> right) => left.CompareTo(right) <= 0;

    public static bool operator >(ConsumerCursor<TScope> left, ConsumerCursor<TScope> right) => left.CompareTo(right) > 0;

    public static bool operator >=(ConsumerCursor<TScope> left, ConsumerCursor<TScope> right) => left.CompareTo(right) >= 0;
}
