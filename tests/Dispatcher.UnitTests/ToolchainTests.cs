using System.Reflection;
using System.Runtime.Versioning;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class ToolchainTests
{
    [Fact]
    public void TestEntryPointTargetsNet10()
    {
        var targetFramework = typeof(ToolchainTests).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>();

        Assert.Equal(10, Environment.Version.Major);
        Assert.NotNull(targetFramework);
        Assert.Equal(".NETCoreApp,Version=v10.0", targetFramework.FrameworkName);
    }
}
