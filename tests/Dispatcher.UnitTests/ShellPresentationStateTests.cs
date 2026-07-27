using Dispatcher.Web;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class ShellPresentationStateTests
{
    [Fact]
    public void
        ThemeAndDensityToggleIndependently()
    {
        var state =
            new ShellPresentationState();
        var changed = 0;
        state.Changed += () => changed++;

        Assert.Equal(
            ShellTheme.Light,
            state.Theme);
        Assert.Equal(
            ShellDensity.Comfortable,
            state.Density);
        Assert.Equal(
            "light",
            state.ThemeToken);
        Assert.Equal(
            "comfortable",
            state.DensityToken);

        state.ToggleTheme();

        Assert.Equal(
            ShellTheme.Dark,
            state.Theme);
        Assert.Equal(
            ShellDensity.Comfortable,
            state.Density);
        Assert.Equal(
            "dark",
            state.ThemeToken);

        state.ToggleDensity();

        Assert.Equal(
            ShellTheme.Dark,
            state.Theme);
        Assert.Equal(
            ShellDensity.Compact,
            state.Density);
        Assert.Equal(
            "compact",
            state.DensityToken);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void
        ReapplyingSamePreferenceDoesNotRaiseChange()
    {
        var state =
            new ShellPresentationState();
        var changed = 0;
        state.Changed += () => changed++;

        state.SetTheme(ShellTheme.Light);
        state.SetDensity(
            ShellDensity.Comfortable);

        Assert.Equal(0, changed);
    }

    [Fact]
    public void UndefinedPreferencesAreRejected()
    {
        var state =
            new ShellPresentationState();

        Assert.Throws<
            ArgumentOutOfRangeException>(
            () => state.SetTheme(
                (ShellTheme)99));
        Assert.Throws<
            ArgumentOutOfRangeException>(
            () => state.SetDensity(
                (ShellDensity)99));
    }
}
