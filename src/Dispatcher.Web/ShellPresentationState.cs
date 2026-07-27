namespace Dispatcher.Web;

public enum ShellTheme
{
    Light = 1,
    Dark = 2,
}

public enum ShellDensity
{
    Comfortable = 1,
    Compact = 2,
}

public sealed class ShellPresentationState
{
    public ShellTheme Theme { get; private set; } =
        ShellTheme.Light;

    public ShellDensity Density { get; private set; } =
        ShellDensity.Comfortable;

    public string ThemeToken =>
        Theme == ShellTheme.Dark
            ? "dark"
            : "light";

    public string DensityToken =>
        Density == ShellDensity.Compact
            ? "compact"
            : "comfortable";

    public string ThemeLabel =>
        Theme == ShellTheme.Dark
            ? "Dark"
            : "Light";

    public string DensityLabel =>
        Density == ShellDensity.Compact
            ? "Compact"
            : "Comfortable";

    public event Action? Changed;

    public void ToggleTheme() =>
        SetTheme(
            Theme == ShellTheme.Light
                ? ShellTheme.Dark
                : ShellTheme.Light);

    public void ToggleDensity() =>
        SetDensity(
            Density == ShellDensity.Comfortable
                ? ShellDensity.Compact
                : ShellDensity.Comfortable);

    public void SetTheme(ShellTheme theme)
    {
        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(
                nameof(theme));
        }

        if (theme == Theme)
        {
            return;
        }

        Theme = theme;
        Changed?.Invoke();
    }

    public void SetDensity(ShellDensity density)
    {
        if (!Enum.IsDefined(density))
        {
            throw new ArgumentOutOfRangeException(
                nameof(density));
        }

        if (density == Density)
        {
            return;
        }

        Density = density;
        Changed?.Invoke();
    }
}
