using Dispatcher.Web;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class EventDispatcherStateTests
{
    private static readonly Guid CurrentSubject =
        Guid.Parse(
            "82000000-0000-0000-0000-000000000001");

    [Fact]
    public void FiltersAndCountersUseTheSameFacetSemantics()
    {
        var state = new EventDispatcherState();
        state.ApplySnapshot(
            new OccurrenceSnapshotPayload(
                4,
                [
                    Occurrence(
                        1,
                        priority: "Critical",
                        condition: "Active",
                        acknowledgement:
                            "Unacknowledged",
                        assignedTo:
                            CurrentSubject),
                    Occurrence(
                        2,
                        priority: "High",
                        condition: "Normal",
                        acknowledgement:
                            "Acknowledged"),
                ]));
        var filter = EventFilter.Default with
        {
            Priority = "Critical",
            Assignment = "Mine",
        };

        var visible = state.Filter(
            filter,
            CurrentSubject,
            DateTimeOffset.UtcNow);
        var counters = state.Count(
            filter,
            CurrentSubject,
            DateTimeOffset.UtcNow);

        Assert.Single(visible);
        Assert.Equal(
            visible.Count,
            counters.Total);
        Assert.Equal(1, counters.Active);
        Assert.Equal(
            1,
            counters.Unacknowledged);
    }

    [Fact]
    public void FeedDeduplicatesProjectionVersionsAndDetectsGap()
    {
        var state = new EventDispatcherState();
        var initial =
            Occurrence(
                1,
                projectionVersion: 5);
        state.ApplySnapshot(
            new OccurrenceSnapshotPayload(
                10,
                [initial]));

        Assert.False(
            state.ApplyFeed(
                new OccurrenceFeedPayload(
                    "Delta",
                    10,
                    11,
                    [
                        initial with
                        {
                            ProjectionVersion = 5,
                            ConditionState =
                                "Normal",
                        },
                    ])));
        Assert.Equal(
            "Active",
            state.Find(initial.OccurrenceId)!
                .ConditionState);

        Assert.False(
            state.ApplyFeed(
                new OccurrenceFeedPayload(
                    "Gap",
                    11,
                    11,
                    [])));
        Assert.True(state.RequiresResnapshot);
    }

    [Fact]
    public void ReturnToNormalDoesNotAcknowledgeOccurrence()
    {
        var state = new EventDispatcherState();
        var occurrence =
            Occurrence(
                1,
                projectionVersion: 1);
        state.ApplySnapshot(
            new OccurrenceSnapshotPayload(
                1,
                [occurrence]));

        state.ApplyFeed(
            new OccurrenceFeedPayload(
                "Delta",
                1,
                2,
                [
                    occurrence with
                    {
                        ProjectionVersion = 2,
                        ConditionState = "Normal",
                        ClosedAt =
                            DateTimeOffset.UtcNow,
                    },
                ]));

        var current =
            state.Find(occurrence.OccurrenceId)!;
        Assert.Equal(
            "Normal",
            current.ConditionState);
        Assert.Equal(
            "Unacknowledged",
            current.AcknowledgementState);
    }

    private static OccurrencePayload Occurrence(
        int seed,
        ulong projectionVersion = 1,
        string priority = "High",
        string condition = "Active",
        string acknowledgement =
            "Unacknowledged",
        Guid? assignedTo = null)
    {
        var occurrenceId =
            new Guid(
                seed,
                0,
                0,
                new byte[8]);
        var pointId =
            new Guid(
                seed + 100,
                0,
                0,
                new byte[8]);
        return new OccurrencePayload(
            projectionVersion,
            occurrenceId,
            pointId,
            priority,
            DateTimeOffset.UtcNow
                .AddMinutes(-5),
            ClosedAt: null,
            condition,
            ConditionPendingSince: null,
            ConditionActiveSince:
                DateTimeOffset.UtcNow
                    .AddMinutes(-5),
            ConditionClearedAt: null,
            ConditionVersion: 1,
            acknowledgement,
            AcknowledgedBy: null,
            AcknowledgedAt: null,
            AcknowledgementVersion: 1,
            assignedTo,
            AssignedAt:
                assignedTo is null
                    ? null
                    : DateTimeOffset.UtcNow,
            AssignmentVersion: 1,
            ShelvedUntil: null,
            ShelvingReason: null,
            ShelvingVersion: 1,
            IsSuppressed: false,
            SuppressionReason: null,
            SuppressionVersion: 1,
            CanAcknowledge: true,
            CanAssign: true,
            CanShelve: true);
    }
}
