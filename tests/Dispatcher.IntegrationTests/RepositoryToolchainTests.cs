using System.Text.Json;
using Xunit;

namespace Dispatcher.IntegrationTests;

public sealed class RepositoryToolchainTests
{
    [Fact]
    public void RepositorySelectsNet10Sdk()
    {
        var globalJsonPath = Path.Combine(AppContext.BaseDirectory, "global.json");
        using var document = JsonDocument.Parse(File.ReadAllText(globalJsonPath));

        var sdk = document.RootElement.GetProperty("sdk");
        var version = sdk.GetProperty("version").GetString();

        Assert.NotNull(version);
        Assert.StartsWith("10.0.", version, StringComparison.Ordinal);
        Assert.Equal("latestFeature", sdk.GetProperty("rollForward").GetString());
    }
}
