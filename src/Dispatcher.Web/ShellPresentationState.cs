using Microsoft.JSInterop;

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

public sealed record ShellPreferenceSnapshot(
    string? Theme,
    string? Density);

public sealed class ShellPresentationState
{
    private IJSRuntime? javaScript;
    private Task? initialization;

    public ShellTheme Theme { get; private set; } =
        ShellTheme.Light;

    public ShellDensity Density { get; private set; } =
        ShellDensity.Comfortable;

    public bool IsInitialized { get; private set; }

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

    public Task InitializeAsync(
        IJSRuntime javaScript)
    {
        ArgumentNullException.ThrowIfNull(javaScript);
        this.javaScript ??= javaScript;
        return initialization ??=
            InitializeCoreAsync();
    }

    public async Task ToggleThemeAsync()
    {
        ToggleTheme();
        await PersistAsync();
    }

    public async Task ToggleDensityAsync()
    {
        ToggleDensity();
        await PersistAsync();
    }

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

    public void Restore(
        string? theme,
        string? density)
    {
        var restoredTheme =
            ParseTheme(theme);
        var restoredDensity =
            ParseDensity(density);
        var changed =
            restoredTheme != Theme ||
            restoredDensity != Density;
        Theme = restoredTheme;
        Density = restoredDensity;
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            var snapshot =
                await javaScript!
                    .InvokeAsync<
                        ShellPreferenceSnapshot?>(
                        "dispatcherUi.readPreferences");
            Restore(
                snapshot?.Theme,
                snapshot?.Density);
        }
        catch (JSException)
        {
            Restore(
                theme: null,
                density: null);
        }

        IsInitialized = true;
        Changed?.Invoke();
    }

    private ValueTask PersistAsync() =>
        javaScript is null
            ? ValueTask.CompletedTask
            : javaScript.InvokeVoidAsync(
                "dispatcherUi.writePreferences",
                ThemeToken,
                DensityToken);

    private static ShellTheme ParseTheme(
        string? value) =>
        string.Equals(
            value,
            "dark",
            StringComparison.OrdinalIgnoreCase)
                ? ShellTheme.Dark
                : ShellTheme.Light;

    private static ShellDensity ParseDensity(
        string? value) =>
        string.Equals(
            value,
            "compact",
            StringComparison.OrdinalIgnoreCase)
                ? ShellDensity.Compact
                : ShellDensity.Comfortable;
}
