using Microsoft.Playwright;
using Xunit;

namespace Dispatcher.BrowserTests;

[Collection(BrowserTestGroup.Name)]
public sealed class C09EventHistoryBrowserTests(
    BrowserServerFixture fixture)
{
    [Fact]
    public async Task
        FiltersAndCountersUseTheVisibleOccurrenceSet()
    {
        await using var scenario =
            await CreateLoggedInScenarioAsync();
        await OpenEventsAsync(scenario);

        Assert.Equal(
            1,
            await scenario.Page
                .Locator(
                    "tr[data-occurrence-id]")
                .CountAsync());
        Assert.Equal(
            "1",
            await scenario.Page
                .Locator(
                    "[aria-label='Filtered event counters'] > div:first-child strong")
                .InnerTextAsync());

        await scenario.Page
            .Locator(
                ".event-toolbar label:has-text('Severity') select")
            .SelectOptionAsync("Low");
        await WaitForHeadingAsync(
            scenario.Page,
            "No matching occurrences");

        Assert.Equal(
            "0",
            await scenario.Page
                .Locator(
                    "[aria-label='Filtered event counters'] > div:first-child strong")
                .InnerTextAsync());
    }

    [Fact]
    public async Task
        OccurrenceActionsKeepFacetsIndependent()
    {
        await using var scenario =
            await CreateLoggedInScenarioAsync();
        await OpenEventsAsync(scenario);
        await OpenPrimaryOccurrenceAsync(
            scenario);

        await scenario.Page
            .GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Acknowledge",
                    Exact = true,
                })
            .ClickAsync();
        await scenario.Page
            .Locator(".event-detail")
            .GetByText(
                "Acknowledged",
                new()
                {
                    Exact = true,
                })
            .WaitForAsync();

        await scenario.Page
            .GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Assign to me",
                    Exact = true,
                })
            .ClickAsync();
        await scenario.Page
            .GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Reassign to me",
                    Exact = true,
                })
            .WaitForAsync();

        await scenario.Page
            .GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Shelve 30 minutes",
                    Exact = true,
                })
            .ClickAsync();
        await scenario.Page
            .GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Unshelve",
                    Exact = true,
                })
            .WaitForAsync();
        await scenario.Page
            .GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Unshelve",
                    Exact = true,
                })
            .ClickAsync();
        await scenario.Page
            .GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Shelve 30 minutes",
                    Exact = true,
                })
            .WaitForAsync();
    }

    [Fact]
    public async Task
        RealtimeAddsRowsAndClearingDoesNotAcknowledge()
    {
        await using var scenario =
            await CreateLoggedInScenarioAsync();
        await OpenEventsAsync(scenario);

        scenario.RaiseNewOccurrence();
        await WaitForCountAsync(
            scenario.Page
                .Locator(
                    "tr[data-occurrence-id]"),
            2);

        scenario.ReturnPrimaryToNormal();
        var primary =
            scenario.Page.Locator(
                $"tr[data-occurrence-id='{BrowserScenario.PrimaryOccurrenceId:D}']");
        await primary
            .GetByText(
                "Normal",
                new()
                {
                    Exact = true,
                })
            .WaitForAsync();

        Assert.True(
            await primary
                .GetByText(
                    "Unacknowledged",
                    new()
                    {
                        Exact = true,
                    })
                .IsVisibleAsync());
    }

    [Fact]
    public async Task
        RealtimeGapTriggersServerResnapshot()
    {
        await using var scenario =
            await CreateLoggedInScenarioAsync();
        await OpenEventsAsync(scenario);

        scenario.CauseEventGap();

        await scenario.Page
            .Locator(
                "[data-realtime-state='resynced']")
            .WaitForAsync();
        Assert.Equal(
            1,
            await scenario.Page
                .Locator(
                    "tr[data-occurrence-id]")
                .CountAsync());
    }

    [Fact]
    public async Task
        HistoryShowsQualityAndGapsWithoutLifecycleActions()
    {
        await using var scenario =
            await CreateLoggedInScenarioAsync();
        await scenario.Page
            .GetByRole(
                AriaRole.Link,
                new()
                {
                    Name = "History",
                    Exact = true,
                })
            .ClickAsync();
        await WaitForHeadingAsync(
            scenario.Page,
            "History");
        await scenario.Page
            .Locator(
                ".history-toolbar label:has-text('Source ID') input")
            .FillAsync(
                BrowserScenario.HistoryStreamSourceId
                    .ToString("D"));
        await scenario.Page
            .Locator(
                ".history-toolbar label:has-text('Point ID') input")
            .FillAsync(
                BrowserScenario.HistoryPointId
                    .ToString("D"));
        await scenario.Page
            .GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Load history",
                    Exact = true,
                })
            .ClickAsync();
        await scenario.Page
            .Locator(
                "[data-history-mode='aggregate']")
            .WaitForAsync();

        Assert.True(
            await scenario.Page
                .Locator(
                    "tr[data-history-gap='true' i]")
                .IsVisibleAsync());
        Assert.Equal(
            0,
            await scenario.Page
                .GetByRole(
                    AriaRole.Button,
                    new()
                    {
                        Name = "Acknowledge",
                        Exact = true,
                    })
                .CountAsync());

        await scenario.Page
            .Locator(
                ".history-toolbar label:has-text('Resolution') select")
            .SelectOptionAsync("Raw");
        await scenario.Page
            .GetByRole(
                AriaRole.Button,
                new()
                {
                    Name = "Load history",
                    Exact = true,
                })
            .ClickAsync();
        await scenario.Page
            .Locator(
                "tr[data-history-kind='gap']")
            .WaitForAsync();
    }

    private async Task<BrowserScenario>
        CreateLoggedInScenarioAsync()
    {
        var scenario =
            await fixture.CreateScenarioAsync();
        scenario.EnableC09();
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

    private static async Task OpenEventsAsync(
        BrowserScenario scenario)
    {
        await scenario.Page
            .GetByRole(
                AriaRole.Link,
                new()
                {
                    Name = "Events",
                    Exact = true,
                })
            .ClickAsync();
        await WaitForHeadingAsync(
            scenario.Page,
            "Event Dispatcher");
        await scenario.Page
            .Locator(
                "tr[data-occurrence-id]")
            .WaitForAsync();
    }

    private static Task
        OpenPrimaryOccurrenceAsync(
            BrowserScenario scenario) =>
        scenario.Page
            .GetByRole(
                AriaRole.Button,
                new()
                {
                    Name =
                        $"Open occurrence {BrowserScenario.PrimaryOccurrenceId:D}",
                    Exact = true,
                })
            .ClickAsync();

    private static async Task WaitForHeadingAsync(
        IPage page,
        string heading)
    {
        try
        {
            await page
                .GetByRole(
                    AriaRole.Heading,
                    new()
                    {
                        Name = heading,
                        Exact = true,
                    })
                .WaitForAsync();
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                $"Heading '{heading}' was not rendered at {page.Url}. Body: {await page.Locator("body").InnerTextAsync()}",
                exception);
        }
    }

    private static async Task WaitForCountAsync(
        ILocator locator,
        int expected)
    {
        await Assertions.Expect(locator)
            .ToHaveCountAsync(expected);
    }
}
