namespace Dispatcher.Semantics;

public readonly record struct CanonicalId<TScope> : IComparable<CanonicalId<TScope>>
{
    private readonly Guid value;

    internal CanonicalId(Guid value)
    {
        this.value = value;
    }

    public bool IsDefined => value != Guid.Empty;

    public Guid Value => IsDefined
        ? value
        : throw new InvalidOperationException("A canonical identity must be defined.");

    public int CompareTo(CanonicalId<TScope> other) => value.CompareTo(other.value);

    public static bool operator <(CanonicalId<TScope> left, CanonicalId<TScope> right) =>
        left.CompareTo(right) < 0;

    public static bool operator <=(CanonicalId<TScope> left, CanonicalId<TScope> right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >(CanonicalId<TScope> left, CanonicalId<TScope> right) =>
        left.CompareTo(right) > 0;

    public static bool operator >=(CanonicalId<TScope> left, CanonicalId<TScope> right) =>
        left.CompareTo(right) >= 0;

    public override string ToString() => value.ToString("D");
}

public static class CanonicalId
{
    public static CanonicalId<TScope> New<TScope>() => new(Guid.CreateVersion7());

    public static CanonicalId<TScope> From<TScope>(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A canonical identity cannot be empty.", nameof(value));
        }

        return new CanonicalId<TScope>(value);
    }

    public static CanonicalId<TScope> Parse<TScope>(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return TryParse<TScope>(text, out var identity)
            ? identity
            : throw new FormatException("The value is not a canonical non-empty GUID.");
    }

    public static bool TryParse<TScope>(string? text, out CanonicalId<TScope> identity)
    {
        if (Guid.TryParseExact(text, "D", out var parsed) && parsed != Guid.Empty)
        {
            identity = new CanonicalId<TScope>(parsed);
            return true;
        }

        identity = default;
        return false;
    }
}
