namespace Dispatcher.Semantics;

public static class MeasurementValue
{
    public const int MaximumFractionalDigits = 9;

    public const decimal AbsoluteLimit = 10_000_000_000_000_000_000m;

    public static bool IsRepresentable(decimal value) =>
        Math.Abs(value) < AbsoluteLimit &&
        value == decimal.Round(
            value,
            MaximumFractionalDigits,
            MidpointRounding.ToEven);

    public static bool TryScale(
        decimal rawValue,
        decimal scale,
        out decimal engineeringValue)
    {
        engineeringValue = 0m;
        if (scale == 0m)
        {
            return false;
        }

        try
        {
            var value = rawValue * scale;
            if (!IsRepresentable(value))
            {
                return false;
            }

            engineeringValue = value;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static decimal RoundDerived(decimal value)
    {
        var rounded = decimal.Round(
            value,
            MaximumFractionalDigits,
            MidpointRounding.ToEven);
        return IsRepresentable(rounded)
            ? rounded
            : throw new OverflowException(
                "Derived measurement exceeds the decimal telemetry envelope.");
    }
}
