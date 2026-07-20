using Dispatcher.Alarm;
using Dispatcher.Semantics;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class AlarmActionContractTests
{
    [Fact]
    public void MaintenanceConstraintRejectsNonUtcWindow()
    {
        Assert.Throws<ArgumentException>(() => new AlarmMaintenanceConstraintSnapshot(
            AlarmOccurrenceId.New(),
            StateVersion.Initial,
            acknowledgementAllowed: true,
            assignmentAllowed: true,
            shelvingAllowed: true,
            new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.FromHours(1))));
    }

}
