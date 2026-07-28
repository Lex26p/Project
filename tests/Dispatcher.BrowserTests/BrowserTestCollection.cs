using Xunit;

namespace Dispatcher.BrowserTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BrowserTestGroup :
    ICollectionFixture<BrowserServerFixture>
{
    public const string Name = "Dispatcher browser";
}
