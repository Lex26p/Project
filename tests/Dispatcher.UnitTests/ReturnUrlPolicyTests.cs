using Dispatcher.Web;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class ReturnUrlPolicyTests
{
    [Theory]
    [InlineData(null, "/home")]
    [InlineData("", "/home")]
    [InlineData("https://example.test", "/home")]
    [InlineData("//example.test/path", "/home")]
    [InlineData("\\\\example.test\\path", "/home")]
    [InlineData("/login", "/home")]
    [InlineData("/login?returnUrl=/history", "/home")]
    [InlineData("/history?point=1", "/history?point=1")]
    [InlineData("/equipment/01234567-89ab-cdef-0123-456789abcdef",
        "/equipment/01234567-89ab-cdef-0123-456789abcdef")]
    public void
        NormalizeAcceptsOnlyLocalNonLoginRoutes(
            string? candidate,
            string expected)
    {
        Assert.Equal(
            expected,
            ReturnUrlPolicy.Normalize(candidate));
    }
}
