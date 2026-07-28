using Microsoft.Playwright;
using Xunit;

namespace Dispatcher.BrowserTests;

[Collection(BrowserTestGroup.Name)]
public sealed class WebShellBrowserTests
{
    private readonly BrowserServerFixture fixture;

    public WebShellBrowserTests(
        BrowserServerFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        this.fixture = fixture;
    }

    [Fact]
    public async Task
        LoginShowsCapabilityFilteredShellAndNavigation()
    {
        await using var scenario =
            await fixture.CreateScenarioAsync();
        await LoginAsync(scenario);

        Assert.True(
            await scenario.Page
                .Locator(".workspace-shell")
                .IsVisibleAsync());
        Assert.True(
            await scenario.Page
                .GetByRole(
                    AriaRole.Link,
                    new()
                    {
                        Name = "Home",
                        Exact = true,
                    })
                .IsVisibleAsync());
        Assert.True(
            await scenario.Page
                .GetByRole(
                    AriaRole.Link,
                    new()
                    {
                        Name = "Current",
                        Exact = true,
                    })
                .IsVisibleAsync());
        Assert.Equal(
            0,
            await scenario.Page
                .GetByRole(
                    AriaRole.Link,
                    new()
                    {
                        Name =
                            "Administration",
                        Exact = true,
                    })
                .CountAsync());
        Assert.True(
            await scenario.Page
                .GetByText(
                    "Browser Operator",
                    new()
                    {
                        Exact = true,
                    })
                .IsVisibleAsync());
    }

    [Fact]
    public async Task
        DirectLoginRouteSurvivesReloadAndReturnsToRequestedPage()
    {
        await using var scenario =
            await fixture.CreateScenarioAsync();
        await scenario.Page.GotoAsync(
            scenario.Url(
                "/login?returnUrl=%2Fhome"));
        await WaitForHeadingAsync(
            scenario.Page,
            "Sign in");

        await scenario.Page.ReloadAsync();
        await WaitForHeadingAsync(
            scenario.Page,
            "Sign in");
        Assert.True(
            await scenario.Page
                .GetByText(
                    "/home",
                    new()
                    {
                        Exact = true,
                    })
                .IsVisibleAsync());

        await SubmitLoginAsync(
            scenario.Page);
        await scenario.Page.WaitForURLAsync(
            "**/home");
        await WaitForHeadingAsync(
            scenario.Page,
            "Home");
    }

    [Fact]
    public async Task
        ForbiddenRouteRendersStableAccessState()
    {
        await using var scenario =
            await fixture.CreateScenarioAsync();
        await LoginAsync(scenario);

        await scenario.Page
            .GetByRole(
                AriaRole.Link,
                new()
                {
                    Name =
                        "Restricted history",
                    Exact = true,
                })
            .ClickAsync();

        await WaitForHeadingAsync(
            scenario.Page,
            "Access denied");
        Assert.True(
            await scenario.Page
                .Locator(
                    "[data-ui-state='forbidden']")
                .IsVisibleAsync());
        Assert.True(
            await scenario.Page
                .Locator(".workspace-shell")
                .IsVisibleAsync());
    }

    [Fact]
    public async Task
        ExpiredSessionInvalidatesClientAndOffersSignIn()
    {
        await using var scenario =
            await fixture.CreateScenarioAsync();
        await LoginAsync(scenario);
        scenario.ExpireSession();

        await scenario.Page
            .GetByRole(
                AriaRole.Link,
                new()
                {
                    Name =
                        "Restricted history",
                    Exact = true,
                })
            .ClickAsync();

        await WaitForHeadingAsync(
            scenario.Page,
            "Session expired");
        Assert.True(
            await scenario.Page
                .Locator(
                    "#main-content [data-ui-state='sessionexpired']")
                .GetByRole(
                    AriaRole.Button,
                    new()
                    {
                        Name = "Sign in",
                        Exact = true,
                    })
                .IsVisibleAsync());
        Assert.Equal(
            0,
            await scenario.Page
                .Locator(
                    ".navigation-link")
                .CountAsync());
    }

    [Fact]
    public async Task
        KioskUsesIsolatedLayoutWithoutWorkspaceNavigation()
    {
        await using var scenario =
            await fixture.CreateScenarioAsync();
        await scenario.Page.GotoAsync(
            scenario.Url("/kiosk"));
        await WaitForHeadingAsync(
            scenario.Page,
            "Plant overview");

        Assert.True(
            await scenario.Page
                .Locator(".kiosk-shell")
                .IsVisibleAsync());
        Assert.True(
            await scenario.Page
                .GetByText(
                    "Read-only display",
                    new()
                    {
                        Exact = true,
                    })
                .IsVisibleAsync());
        Assert.Equal(
            0,
            await scenario.Page
                .Locator(".shell-navigation")
                .CountAsync());
        Assert.Equal(
            0,
            await scenario.Page
                .Locator(".workspace-search")
                .CountAsync());
        Assert.Equal(
            0,
            await scenario.Page
                .Locator(".session-link")
                .CountAsync());
    }

    [Fact]
    public async Task
        DesktopLayoutFits1440By900WithoutCriticalOverflow()
    {
        await using var scenario =
            await fixture.CreateScenarioAsync();
        await LoginAsync(scenario);

        var metrics =
            await scenario.Page
                .EvaluateAsync<LayoutMetrics>(
                    """
                    () => {
                        const root =
                            document.documentElement;
                        const shell =
                            document.querySelector(
                                ".workspace-shell");
                        const main =
                            document.querySelector(
                                ".shell-main");
                        return {
                            viewportWidth:
                                window.innerWidth,
                            viewportHeight:
                                window.innerHeight,
                            documentWidth:
                                root.scrollWidth,
                            documentHeight:
                                root.scrollHeight,
                            shellWidth:
                                shell?.getBoundingClientRect()
                                    .width ?? 0,
                            shellHeight:
                                shell?.getBoundingClientRect()
                                    .height ?? 0,
                            mainWidth:
                                main?.getBoundingClientRect()
                                    .width ?? 0
                        };
                    }
                    """);

        Assert.InRange(
            metrics.DocumentWidth,
            0,
            metrics.ViewportWidth + 1);
        Assert.InRange(
            metrics.DocumentHeight,
            0,
            metrics.ViewportHeight + 1);
        Assert.InRange(
            metrics.ShellWidth,
            metrics.ViewportWidth - 1,
            metrics.ViewportWidth + 1);
        Assert.InRange(
            metrics.ShellHeight,
            metrics.ViewportHeight - 1,
            metrics.ViewportHeight + 1);
        Assert.True(
            metrics.MainWidth >= 500,
            $"Main content width was {metrics.MainWidth}.");
    }

    private static async Task LoginAsync(
        BrowserScenario scenario)
    {
        await scenario.Page.GotoAsync(
            scenario.Url("/login"));
        await WaitForHeadingAsync(
            scenario.Page,
            "Sign in");
        await SubmitLoginAsync(
            scenario.Page);
        await scenario.Page.WaitForURLAsync(
            "**/home");
        await WaitForHeadingAsync(
            scenario.Page,
            "Home");
        await scenario.Page
            .Locator(".navigation-link")
            .First
            .WaitForAsync(
                new LocatorWaitForOptions
                {
                    State =
                        WaitForSelectorState.Visible,
                });
    }

    private static async Task SubmitLoginAsync(
        IPage page)
    {
        await page
            .Locator(
                "input[autocomplete='username']")
            .FillAsync(
                "browser-operator");
        await page
            .Locator(
                "input[autocomplete='current-password']")
            .FillAsync(
                "BrowserPassword123!");
        await page
            .Locator(
                "form.auth-form button[type='submit']")
            .ClickAsync();
    }

    private static Task WaitForHeadingAsync(
        IPage page,
        string heading) =>
        page
            .GetByRole(
                AriaRole.Heading,
                new()
                {
                    Name = heading,
                    Exact = true,
                })
            .WaitForAsync(
                new LocatorWaitForOptions
                {
                    State =
                        WaitForSelectorState.Visible,
                });

    private sealed class LayoutMetrics
    {
        public double ViewportWidth { get; set; }

        public double ViewportHeight { get; set; }

        public double DocumentWidth { get; set; }

        public double DocumentHeight { get; set; }

        public double ShellWidth { get; set; }

        public double ShellHeight { get; set; }

        public double MainWidth { get; set; }
    }
}
