namespace Dispatcher.Semantics;

public sealed record TypedValue<TValue>
{
    internal TypedValue(TValue value)
    {
        Value = value;
    }

    public TValue Value { get; }

}

public static class TypedValue
{
    public static TypedValue<TValue> From<TValue>(TValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new TypedValue<TValue>(value);
    }
}

public sealed record Unit
{
    private Unit(string symbol)
    {
        Symbol = symbol;
    }

    public string Symbol { get; }

    public static Unit None { get; } = new("1");

    public static Unit FromSymbol(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        return new Unit(symbol.Trim());
    }

    public override string ToString() => Symbol;
}

public enum DataQuality
{
    Unknown = 0,
    Good = 1,
    Uncertain = 2,
    Bad = 3,
}

public enum Freshness
{
    Unknown = 0,
    Fresh = 1,
    Stale = 2,
}
