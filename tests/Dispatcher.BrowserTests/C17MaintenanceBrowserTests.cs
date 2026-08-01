using Microsoft.Playwright;
using Xunit;

namespace Dispatcher.BrowserTests;

[Collection(BrowserTestGroup.Name)]
public sealed class C17MaintenanceBrowserTests(BrowserServerFixture fixture)
{
    [Fact]
    public async Task MaintenanceWorkspaceDeepLinksAndLifecycleUseAllowedActions()
    {
        await using var scenario = await fixture.CreateScenarioAsync();
        scenario.EnableC14();
        scenario.EnableC17();
        await SignInAsync(scenario);

        await scenario.Page.GetByRole(
            AriaRole.Link,
            new() { Name = "Maintenance", Exact = true }).ClickAsync();
        var heading = scenario.Page.GetByRole(
            AriaRole.Heading,
            new() { Name = "Maintenance", Exact = true });
        await heading.WaitForAsync();
        await scenario.Page.GetByText(
            "Requires assignment",
            new() { Exact = true }).WaitForAsync();

        await scenario.Page.GetByRole(
            AriaRole.Button,
            new() { Name = "Assets", Exact = true }).ClickAsync();
        await scenario.Page.GetByText(
            "Review required",
            new() { Exact = true }).WaitForAsync();
        Assert.Equal(
            $"/equipment/{BrowserScenario.HistoryPointId:D}?scopeId={BrowserScenario.CommissioningScopeId:D}",
            await scenario.Page.GetByRole(
                AriaRole.Link,
                new() { Name = "Open equipment", Exact = true }).GetAttributeAsync("href"));

        await scenario.Page.GetByRole(
            AriaRole.Button,
            new() { Name = "Calendar & forecast", Exact = true }).ClickAsync();
        var forecastLinks = scenario.Page.GetByRole(
            AriaRole.Link,
            new() { Name = "Open work order", Exact = true });
        await forecastLinks.First.WaitForAsync();
        Assert.Equal(2, await forecastLinks.CountAsync());

        await scenario.Page.GetByRole(
            AriaRole.Button,
            new() { Name = "Work", Exact = true }).ClickAsync();
        var workOrderLink = scenario.Page.Locator(
            "a[href='/maintenance/work-orders/81000000-0000-0000-0000-000000000033']");
        await workOrderLink.WaitForAsync();
        Assert.Equal(1, await workOrderLink.CountAsync());
        await workOrderLink.ClickAsync();

        await scenario.Page.GetByRole(
            AriaRole.Button,
            new() { Name = "Claim", Exact = true }).ClickAsync();
        await scenario.Page.GetByRole(
            AriaRole.Button,
            new() { Name = "Accept and acknowledge safety", Exact = true }).ClickAsync();
        await scenario.Page.GetByRole(
            AriaRole.Button,
            new() { Name = "Start", Exact = true }).ClickAsync();

        var checklist = scenario.Page.Locator(".maintenance-checklist input[type='checkbox']");
        await checklist.WaitForAsync();
        Assert.Equal(1, await checklist.CountAsync());
        await checklist.CheckAsync();
        await scenario.Page.GetByRole(
            AriaRole.Button,
            new() { Name = "Submit for acceptance", Exact = true }).ClickAsync();
        await scenario.Page.GetByRole(
            AriaRole.Button,
            new() { Name = "Accept result", Exact = true }).ClickAsync();

        var currentState = scenario.Page.Locator(".maintenance-lifecycle .is-current");
        await currentState.WaitForAsync();
        Assert.Equal("Completed", await currentState.InnerTextAsync());
        Assert.Equal(0, await scenario.Page.GetByRole(
            AriaRole.Button,
            new() { Name = "Start", Exact = true }).CountAsync());
    }

    private static async Task SignInAsync(BrowserScenario scenario)
    {
        await scenario.Page.GotoAsync(scenario.Url("/login"));
        await scenario.Page.Locator("input[autocomplete='username']").FillAsync("browser-operator");
        await scenario.Page.Locator("input[autocomplete='current-password']")
            .FillAsync("BrowserPassword123!");
        await scenario.Page.Locator("form.auth-form button[type='submit']").ClickAsync();
        await scenario.Page.WaitForURLAsync("**/home");
    }
}
