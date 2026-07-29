using Microsoft.Playwright;
using Xunit;

namespace Dispatcher.BrowserTests;

[Collection(BrowserTestGroup.Name)]
public sealed class C16IncidentMyWorkBrowserTests(BrowserServerFixture fixture)
{
    [Fact]
    public async Task IncidentAndTaskWorkflowRemainIndependentFromAlarmAcknowledgement()
    {
        await using var scenario = await fixture.CreateScenarioAsync();
        scenario.EnableC09();
        scenario.EnableC16();
        await SignInAsync(scenario);

        await scenario.Page.GetByRole(
            AriaRole.Link,
            new() { Name = "Events", Exact = true }).ClickAsync();
        await scenario.Page.GetByRole(
            AriaRole.Heading,
            new() { Name = "Event Dispatcher", Exact = true }).WaitForAsync();
        await scenario.Page.GetByRole(
            AriaRole.Button,
            new()
            {
                Name = $"Open occurrence {BrowserScenario.PrimaryOccurrenceId:D}",
                Exact = true,
            }).ClickAsync();
        Assert.True(await scenario.Page.Locator(".event-detail").GetByText(
            "Unacknowledged",
            new() { Exact = true }).IsVisibleAsync());

        await scenario.Page.GetByRole(
            AriaRole.Button,
            new() { Name = "Create incident", Exact = true }).ClickAsync();
        await scenario.Page.WaitForURLAsync("**/incidents/*");
        await scenario.Page.GetByRole(
            AriaRole.Heading,
            new() { Name = "Incident", Exact = true }).WaitForAsync();
        Assert.True(await scenario.Page.GetByRole(
            AriaRole.Link,
            new() { Name = "Event 81000000", Exact = true }).IsVisibleAsync());

        await scenario.Page.GetByRole(
            AriaRole.Link,
            new() { Name = "My Work", Exact = true }).ClickAsync();
        await scenario.Page.GetByRole(
            AriaRole.Heading,
            new() { Name = "My Work", Exact = true }).WaitForAsync();
        await scenario.Page.Locator("[data-task-id]").ClickAsync();
        await scenario.Page.GetByRole(
            AriaRole.Button,
            new() { Name = "Accept", Exact = true }).ClickAsync();
        await scenario.Page.Locator(".my-work-detail").GetByText(
            "Accepted",
            new() { Exact = true }).WaitForAsync();

        await scenario.Page.GetByLabel("Reason").FillAsync("Needs specialist");
        await scenario.Page.GetByLabel("Transfer candidate").FillAsync("engineer");
        await scenario.Page.GetByRole(
            AriaRole.Button,
            new() { Name = "Find", Exact = true }).ClickAsync();
        await scenario.Page.Locator(".my-work-actions select")
            .SelectOptionAsync(new SelectOptionValue { Label = "Permitted engineer" });
        Assert.Equal(
            1,
            await scenario.Page.Locator(".my-work-actions select option:not([value=''])").CountAsync());
        await scenario.Page.GetByRole(
            AriaRole.Button,
            new() { Name = "Transfer", Exact = true }).ClickAsync();
        await scenario.Page.GetByRole(
            AriaRole.Heading,
            new() { Name = "No matching assignments", Exact = true }).WaitForAsync();

        await scenario.Page.GetByRole(
            AriaRole.Link,
            new() { Name = "Events", Exact = true }).ClickAsync();
        await scenario.Page.GetByRole(
            AriaRole.Button,
            new()
            {
                Name = $"Open occurrence {BrowserScenario.PrimaryOccurrenceId:D}",
                Exact = true,
            }).ClickAsync();
        Assert.True(await scenario.Page.Locator(".event-detail").GetByText(
            "Unacknowledged",
            new() { Exact = true }).IsVisibleAsync());
    }

    private static async Task SignInAsync(BrowserScenario scenario)
    {
        await scenario.Page.GotoAsync(scenario.Url("/login"));
        await scenario.Page.GetByRole(
            AriaRole.Heading,
            new() { Name = "Sign in", Exact = true }).WaitForAsync();
        await scenario.Page.Locator("input[autocomplete='username']").FillAsync("browser-operator");
        await scenario.Page.Locator("input[autocomplete='current-password']")
            .FillAsync("BrowserPassword123!");
        await scenario.Page.Locator("form.auth-form button[type='submit']").ClickAsync();
        await scenario.Page.WaitForURLAsync("**/home");
    }
}
