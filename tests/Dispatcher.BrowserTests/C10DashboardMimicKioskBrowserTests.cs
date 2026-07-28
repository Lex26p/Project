using Microsoft.Playwright;
using Xunit;

namespace Dispatcher.BrowserTests;

[Collection(BrowserTestGroup.Name)]
public sealed class
    C10DashboardMimicKioskBrowserTests(
        BrowserServerFixture fixture)
{
    [Fact]
    public async Task
        CatalogOpensExactWindowWithCurrentHistoryEventsAndSafeMimic()
    {
        await using var scenario =
            await CreateLoggedInScenarioAsync();
        await OpenRuntimeAsync(scenario);

        await scenario.Page
            .Locator(
                "[aria-label='Current values']")
            .GetByText(
                "42 °C",
                new()
                {
                    Exact = false,
                })
            .WaitForAsync();
        await scenario.Page
            .Locator(
                "[aria-label='History trend'] tbody tr")
            .WaitForAsync();
        Assert.Equal(
            "1",
            await scenario.Page
                .Locator(
                    "[aria-label='Event indicator'] .event-indicator__count")
                .InnerTextAsync());
        await scenario.Page
            .Locator(
                $"[data-binding-id='{BrowserScenario.RuntimeMimicBindingId:D}'][data-quality='Good']")
            .WaitForAsync();
    }

    [Fact]
    public async Task
        CurrentDeltaAndPublishedRevisionResynchronizeWholeRuntime()
    {
        await using var scenario =
            await CreateLoggedInScenarioAsync();
        await OpenRuntimeAsync(scenario);

        scenario.UpdateDashboardCurrent(84);
        await scenario.Page
            .Locator(
                "[aria-label='Current values']")
            .GetByText(
                "84 °C",
                new()
                {
                    Exact = false,
                })
            .WaitForAsync();
        await scenario.Page
            .Locator(
                $"[data-binding-id='{BrowserScenario.RuntimeMimicBindingId:D}'][data-value='84']")
            .WaitForAsync();

        scenario.PublishDashboardRevision();
        await scenario.Page
            .Locator(
                "[data-dashboard-resync='true']")
            .WaitForAsync();
        await scenario.Page
            .Locator(
                $"[data-dashboard-revision='{scenario.PublishedDashboardRevisionId:D}']")
            .WaitForAsync();
    }

    [Fact]
    public async Task
        DashboardDraftRequiresSaveAndValidationBeforePublish()
    {
        await using var scenario =
            await CreateLoggedInScenarioAsync();
        await OpenCatalogAsync(scenario);
        await scenario.Page
            .GetByRole(
                AriaRole.Link,
                new()
                {
                    Name = "Edit",
                    Exact = true,
                })
            .ClickAsync();
        await WaitForHeadingAsync(
            scenario.Page,
            "Dashboard Editor");

        await scenario.Page
            .GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Add window",
                    Exact = true,
                })
            .ClickAsync();
        await scenario.Page
            .GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Add widget binding",
                    Exact = true,
                })
            .ClickAsync();
        var publish =
            scenario.Page.GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Publish",
                    Exact = true,
                });
        Assert.True(
            await publish.IsDisabledAsync());

        await scenario.Page
            .GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Save",
                    Exact = true,
                })
            .ClickAsync();
        await scenario.Page
            .GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Validate",
                    Exact = true,
                })
            .ClickAsync();
        await scenario.Page
            .Locator(
                "[aria-label='Publication impact']")
            .WaitForAsync();
        Assert.False(
            await publish.IsDisabledAsync());
        await publish.ClickAsync();
        await scenario.Page
            .GetByText(
                "Dashboard published.",
                new()
                {
                    Exact = true,
                })
            .WaitForAsync();
    }

    [Fact]
    public async Task
        UnsafeSvgIsRejectedBeforePreviewOrPublication()
    {
        await using var scenario =
            await CreateLoggedInScenarioAsync();
        await OpenRuntimeAsync(scenario);
        await scenario.Page
            .GetByRole(
                AriaRole.Link,
                new()
                {
                    Name = "Edit Mimic",
                    Exact = true,
                })
            .ClickAsync();
        await WaitForHeadingAsync(
            scenario.Page,
            "SVG Mimic Editor");
        await scenario.Page
            .Locator("textarea")
            .FillAsync(
                "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        await scenario.Page
            .GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Preview",
                    Exact = true,
                })
            .ClickAsync();
        await WaitForHeadingAsync(
            scenario.Page,
            "Validation errors");
        Assert.Equal(
            0,
            await scenario.Page
                .Locator(
                    "[aria-label='Sanitized Mimic preview'] script")
                .CountAsync());
    }

    [Fact]
    public async Task
        KioskRetainsApprovedOfflineContentResyncsAndClearsOnRevoke()
    {
        await using var scenario =
            await fixture.CreateScenarioAsync();
        scenario.EnableC10();
        await scenario.Page.GotoAsync(
            scenario.Url("/kiosk"));
        await WaitForHeadingAsync(
            scenario.Page,
            "Operations overview");

        scenario.SetKioskOffline(true);
        await scenario.Page
            .Locator(
                "[data-kiosk-online='false']")
            .WaitForAsync();
        Assert.True(
            await scenario.Page
                .GetByText(
                    "Offline — read-only last synchronized content.",
                    new()
                    {
                        Exact = true,
                    })
                .IsVisibleAsync());

        scenario.RestoreKiosk();
        await scenario.Page
            .Locator(
                "[data-kiosk-online='true']")
            .WaitForAsync();
        scenario.PublishDashboardRevision();
        await scenario.Page
            .Locator(
                $"[data-kiosk-revision='{scenario.PublishedDashboardRevisionId:D}']")
            .WaitForAsync();

        scenario.RevokeKiosk();
        await WaitForHeadingAsync(
            scenario.Page,
            "Terminal content unavailable");
        await scenario.Page
            .GetByText(
                "Terminal identity is blocked or revoked.",
                new()
                {
                    Exact = true,
                })
            .WaitForAsync();
    }

    private async Task<BrowserScenario>
        CreateLoggedInScenarioAsync()
    {
        var scenario =
            await fixture.CreateScenarioAsync();
        scenario.EnableC10();
        await scenario.Page.GotoAsync(
            scenario.Url("/login"));
        await WaitForHeadingAsync(
            scenario.Page,
            "Sign in");
        await scenario.Page
            .Locator(
                "input[autocomplete='username']")
            .FillAsync(
                "browser-operator");
        await scenario.Page
            .Locator(
                "input[autocomplete='current-password']")
            .FillAsync(
                "BrowserPassword123!");
        await scenario.Page
            .Locator(
                "form.auth-form button[type='submit']")
            .ClickAsync();
        await scenario.Page.WaitForURLAsync(
            "**/home");
        return scenario;
    }

    private static async Task OpenCatalogAsync(
        BrowserScenario scenario)
    {
        await scenario.Page
            .GetByRole(
                AriaRole.Link,
                new()
                {
                    Name = "Dashboards",
                    Exact = true,
                })
            .ClickAsync();
        await WaitForHeadingAsync(
            scenario.Page,
            "Dashboards");
        await scenario.Page
            .Locator(
                $"[data-dashboard-id='{BrowserScenario.RuntimeDashboardId:D}']")
            .WaitForAsync();
    }

    private static async Task OpenRuntimeAsync(
        BrowserScenario scenario)
    {
        await OpenCatalogAsync(scenario);
        await scenario.Page
            .GetByRole(
                AriaRole.Link,
                new()
                {
                    Name = "Open",
                    Exact = true,
                })
            .ClickAsync();
        await scenario.Page.WaitForURLAsync(
            $"**/d/{BrowserScenario.RuntimeDashboardId:D}/{BrowserScenario.RuntimeWindowId:D}");
        await WaitForHeadingAsync(
            scenario.Page,
            "Operations overview");
    }

    private static Task WaitForHeadingAsync(
        IPage page,
        string heading) =>
        page.GetByRole(
                AriaRole.Heading,
                new()
                {
                    Name = heading,
                    Exact = true,
                })
            .WaitForAsync();
}
