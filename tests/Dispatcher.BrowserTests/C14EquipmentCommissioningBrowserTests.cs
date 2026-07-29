using System.Text;
using Microsoft.Playwright;
using Xunit;

namespace Dispatcher.BrowserTests;

[Collection(BrowserTestGroup.Name)]
public sealed class C14EquipmentCommissioningBrowserTests(
    BrowserServerFixture fixture)
{
    [Fact]
    public async Task ManualRowsShowLocalErrorsAndSupportCopyAndTemplates()
    {
        await using var scenario = await CreateLoggedInScenarioAsync();
        await OpenEditorAsync(scenario);

        await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "Add Modbus TCP" }).ClickAsync();
        await FillCommonAsync(scenario.Page, "PLC-C14", "Packaging PLC", "192.0.2.10");
        await scenario.Page.GetByTestId("save-device").ClickAsync();
        await scenario.Page.GetByText("Draft saved.", new() { Exact = true }).WaitForAsync();

        await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "PLC-C14" }).ClickAsync();
        await scenario.Page.GetByTestId("device-code").FillAsync(string.Empty);
        await scenario.Page.GetByTestId("save-device").ClickAsync();
        await scenario.Page.GetByText(
            "Equipment code is required.",
            new() { Exact = false }).WaitForAsync();
        await scenario.Page.GetByTestId("device-code").FillAsync("PLC-C14");
        await scenario.Page.GetByTestId("save-device").ClickAsync();

        await scenario.Page.GetByLabel("Quantity").FillAsync("1");
        await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "Copy", Exact = true }).ClickAsync();
        await scenario.Page.GetByText(
            "Copies created. Hosts and secrets were not changed/copied.",
            new() { Exact = true }).WaitForAsync();

        await scenario.Page.GetByLabel("Name").Last.FillAsync("PLC template");
        await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "Save as template" }).ClickAsync();
        await scenario.Page.GetByText(
            "Template saved without identity, host, Unit ID or secret.",
            new() { Exact = true }).WaitForAsync();

        await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "Add SNMP v2c" }).ClickAsync();
        await FillCommonAsync(scenario.Page, "SW-C14", "Packaging switch", "192.0.2.20");
        await scenario.Page.GetByTestId("save-device").ClickAsync();
        await scenario.Page.GetByText("Draft saved.", new() { Exact = true }).WaitForAsync();
        Assert.Equal(3, await scenario.Page.Locator("[aria-label='Staging rows'] tbody tr").CountAsync());
    }

    [Fact]
    public async Task CsvDiagnosticRefreshExplicitUpdateAndPublicationRemainVisible()
    {
        await using var scenario = await CreateLoggedInScenarioAsync();
        await OpenEditorAsync(scenario);

        var csv = "row_id,equipment_id,scope_id,location_id,code,name,protocol,host,port,modbus_unit_id,snmp_version,secret\n" +
                  $"{Guid.NewGuid()},{Guid.NewGuid()},{BrowserScenario.CommissioningScopeId},{BrowserScenario.CommissioningDefaultLocationId},CSV-PLC,CSV PLC,modbus_tcp,127.0.0.1,502,1,,\n" +
                  $"{Guid.NewGuid()},{Guid.NewGuid()},{BrowserScenario.CommissioningScopeId},{BrowserScenario.CommissioningDefaultLocationId},BAD,Bad,snmp,127.0.0.1,70000,,v3,";
        await scenario.Page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "devices.csv",
            MimeType = "text/csv",
            Buffer = Encoding.UTF8.GetBytes(csv),
        });
        await scenario.Page.GetByText(
            "CSV: 1 row(s) staged, 1 row error(s).",
            new() { Exact = true }).WaitForAsync();

        await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "CSV-PLC" }).ClickAsync();
        await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "Run sample poll" }).ClickAsync();
        await scenario.Page.GetByTestId("diagnostic-result").GetByText("Succeeded", new() { Exact = true }).WaitForAsync();

        await scenario.Page.ReloadAsync();
        await LoginAsync(scenario);
        await OpenEditorAsync(scenario);
        await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "CSV-PLC" }).ClickAsync();
        await scenario.Page.GetByTestId("diagnostic-result").WaitForAsync();

        await scenario.Page.GetByLabel("Apply action").SelectOptionAsync("Update");
        await scenario.Page.GetByTestId("save-device").ClickAsync();
        await scenario.Page.GetByTestId("diagnostic-result")
            .GetByText("Stale result", new() { Exact = true }).WaitForAsync();
        await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "Authorize update" }).ClickAsync();
        await scenario.Page.GetByText("Update explicitly authorized.", new() { Exact = true }).WaitForAsync();
        await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "Apply valid rows" }).ClickAsync();

        await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "Save revision" }).ClickAsync();
        Assert.False(await scenario.Page.GetByRole(
            AriaRole.Button,
            new() { Name = "Publish", Exact = true }).IsEnabledAsync());
        await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "Validate" }).ClickAsync();
        await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "Publish" }).ClickAsync();
        await scenario.Page.GetByTestId("configuration-status")
            .GetByText("activated", new() { Exact = false })
            .WaitForAsync();
        Assert.DoesNotContain(
            "no",
            await scenario.Page.GetByTestId("configuration-status").InnerTextAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiagnosticPendingAndTimeoutAreVisible()
    {
        await using var scenario = await CreateLoggedInScenarioAsync();
        scenario.SetC14DiagnosticOutcome(1, null, null);
        await OpenEditorAsync(scenario);

        await scenario.Page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "devices.csv",
            MimeType = "text/csv",
            Buffer = Encoding.UTF8.GetBytes("code,name,protocol,host,port\nCSV-PLC,CSV PLC,modbus_tcp,127.0.0.1,502"),
        });
        await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "CSV-PLC" }).ClickAsync();
        await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "Check connection" }).ClickAsync();
        await scenario.Page.GetByText(
            "Diagnostic is still pending and can be refreshed later.",
            new() { Exact = true }).WaitForAsync(new() { Timeout = 15_000 });
        await scenario.Page.GetByTestId("diagnostic-result")
            .GetByText("Pending", new() { Exact = true }).WaitForAsync();

        scenario.SetC14DiagnosticOutcome(
            5,
            "diagnostic.timeout",
            "Diagnostic timed out.");
        await scenario.Page.GetByRole(AriaRole.Button, new() { Name = "Run sample poll" }).ClickAsync();
        await scenario.Page.GetByTestId("diagnostic-result")
            .GetByText("Timed out", new() { Exact = true }).WaitForAsync();
    }

    private async Task<BrowserScenario> CreateLoggedInScenarioAsync()
    {
        var scenario = await fixture.CreateScenarioAsync();
        scenario.EnableC14();
        await LoginAsync(scenario);
        return scenario;
    }

    private static async Task LoginAsync(BrowserScenario scenario)
    {
        await scenario.Page.GotoAsync(scenario.Url("/login"));
        await scenario.Page.GetByRole(
            AriaRole.Heading,
            new() { Name = "Sign in", Exact = true }).WaitForAsync();
        await scenario.Page.Locator("input[autocomplete='username']").FillAsync("browser-operator");
        await scenario.Page.Locator("input[autocomplete='current-password']").FillAsync("BrowserPassword123!");
        await scenario.Page.Locator("form.auth-form button[type='submit']").ClickAsync();
        await scenario.Page.WaitForURLAsync("**/home");
    }

    private static async Task OpenEditorAsync(BrowserScenario scenario)
    {
        await scenario.Page.GetByRole(
            AriaRole.Link,
            new() { Name = "Equipment", Exact = true }).ClickAsync();
        await scenario.Page.GetByRole(
            AriaRole.Heading,
            new() { Name = "Equipment", Exact = true }).WaitForAsync();
        await scenario.Page.GetByRole(
            AriaRole.Link,
            new() { Name = "Add or commission devices", Exact = true }).ClickAsync();
        await scenario.Page.GetByRole(
            AriaRole.Heading,
            new() { Name = "Device editor", Exact = true }).WaitForAsync();

        await scenario.Page.GetByLabel("Default location ID")
            .FillAsync(BrowserScenario.CommissioningDefaultLocationId.ToString("D"));
    }

    private static async Task FillCommonAsync(
        IPage page,
        string code,
        string name,
        string host)
    {
        await page.GetByTestId("device-code").FillAsync(code);
        await page.GetByLabel("Name").First.FillAsync(name);
        await page.GetByTestId("device-host").FillAsync(host);
    }

}
