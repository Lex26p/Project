namespace Dispatcher.Web;

public sealed class EventDispatcherState
{
    private readonly Dictionary<
        Guid,
        OccurrencePayload> occurrences = [];

    public ulong Cursor { get; private set; }

    public bool RequiresResnapshot { get; private set; }

    public IReadOnlyList<OccurrencePayload>
        Occurrences =>
        occurrences.Values
            .OrderByDescending(
                item => item.OpenedAt)
            .ThenBy(
                item => item.OccurrenceId)
            .ToArray();

    public void ApplySnapshot(
        OccurrenceSnapshotPayload snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        occurrences.Clear();
        foreach (var occurrence in
                 snapshot.Occurrences)
        {
            ApplyOccurrence(occurrence);
        }

        Cursor = snapshot.Cursor;
        RequiresResnapshot = false;
    }

    public bool ApplyFeed(
        OccurrenceFeedPayload feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        if (string.Equals(
                feed.Kind,
                "Gap",
                StringComparison.OrdinalIgnoreCase) ||
            feed.From != Cursor)
        {
            RequiresResnapshot = true;
            return false;
        }

        var changed = false;
        foreach (var occurrence in
                 feed.Changes)
        {
            changed |=
                ApplyOccurrence(occurrence);
        }

        Cursor = feed.To;
        return changed;
    }

    public IReadOnlyList<OccurrencePayload> Filter(
        EventFilter filter,
        Guid? currentSubjectId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return Occurrences
            .Where(item =>
                Matches(
                    item,
                    filter,
                    currentSubjectId,
                    now))
            .ToArray();
    }

    public EventViewCounters Count(
        EventFilter filter,
        Guid? currentSubjectId,
        DateTimeOffset now)
    {
        var items = Filter(
            filter,
            currentSubjectId,
            now);
        return new EventViewCounters(
            items.Count,
            items.Count(item => item.IsActive),
            items.Count(item =>
                string.Equals(
                    item.AcknowledgementState,
                    "Unacknowledged",
                    StringComparison.Ordinal)),
            items.Count(item =>
                item.IsShelved(now)));
    }

    public OccurrencePayload? Find(
        Guid occurrenceId) =>
        occurrences.GetValueOrDefault(
            occurrenceId);

    public void Clear()
    {
        occurrences.Clear();
        Cursor = 0;
        RequiresResnapshot = true;
    }

    private bool ApplyOccurrence(
        OccurrencePayload occurrence)
    {
        if (!occurrences.TryGetValue(
                occurrence.OccurrenceId,
                out var current) ||
            occurrence.ProjectionVersion >
                current.ProjectionVersion)
        {
            occurrences[
                occurrence.OccurrenceId] =
                occurrence;
            return true;
        }

        return false;
    }

    private static bool Matches(
        OccurrencePayload item,
        EventFilter filter,
        Guid? currentSubjectId,
        DateTimeOffset now)
    {
        if (!MatchesToken(
                filter.Priority,
                item.Priority) ||
            !MatchesToken(
                filter.Condition,
                item.ConditionState) ||
            !MatchesToken(
                filter.Acknowledgement,
                item.AcknowledgementState))
        {
            return false;
        }

        if (filter.Assignment switch
            {
                "Assigned" =>
                    item.AssignedTo is not null,
                "Unassigned" =>
                    item.AssignedTo is null,
                "Mine" =>
                    currentSubjectId is not null &&
                    item.AssignedTo ==
                    currentSubjectId,
                _ => true,
            } is false)
        {
            return false;
        }

        if (filter.Special switch
            {
                "Shelved" =>
                    item.IsShelved(now),
                "Suppressed" =>
                    item.IsSuppressed,
                "Normal queue" =>
                    !item.IsShelved(now) &&
                    !item.IsSuppressed,
                _ => true,
            } is false)
        {
            return false;
        }

        var text = filter.Text.Trim();
        return text.Length == 0 ||
               item.OccurrenceId.ToString()
                   .Contains(
                       text,
                       StringComparison
                           .OrdinalIgnoreCase) ||
               item.PointId.ToString()
                   .Contains(
                       text,
                       StringComparison
                           .OrdinalIgnoreCase) ||
               item.Priority.Contains(
                   text,
                   StringComparison
                       .OrdinalIgnoreCase);
    }

    private static bool MatchesToken(
        string filter,
        string value) =>
        string.Equals(
            filter,
            "All",
            StringComparison.Ordinal) ||
        string.Equals(
            filter,
            value,
            StringComparison.Ordinal);
}
