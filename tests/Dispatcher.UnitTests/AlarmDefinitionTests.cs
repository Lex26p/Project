using Dispatcher.Alarm;
using Dispatcher.Core;
using Dispatcher.Semantics;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class AlarmDefinitionTests
{
    [Fact]
    public void DefinitionSetRejectsDuplicateIdentity()
    {
        var scopeId = RuntimeScopeId.From(Guid.Parse("d1000000-0000-7000-8000-000000000001"));
        var pointId = PointId.From(Guid.Parse("d2000000-0000-7000-8000-000000000001"));
        var definitionId = AlarmDefinitionId.From(Guid.Parse("d3000000-0000-7000-8000-000000000001"));
        var definition = new AlarmDefinition(
            definitionId,
            pointId,
            "High power",
            AlarmThresholdDirection.High,
            100,
            10,
            TimeSpan.Zero,
            TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => new AlarmDefinitionSet(
            scopeId,
            RevisionNumber.Initial,
            [definition, definition]));
    }
}
