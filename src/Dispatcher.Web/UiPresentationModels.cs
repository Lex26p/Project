namespace Dispatcher.Web;

public enum UiStateKind
{
    Loading = 1,
    Empty = 2,
    Partial = 3,
    Stale = 4,
    Offline = 5,
    Forbidden = 6,
    SessionExpired = 7,
    NotFound = 8,
    Error = 9,
}

public enum UiBadgeCategory
{
    State = 1,
    Severity = 2,
    Acknowledgement = 3,
    Assignment = 4,
    Quality = 5,
    Freshness = 6,
}

public enum UiBadgeTone
{
    Neutral = 1,
    Information = 2,
    Positive = 3,
    Warning = 4,
    Critical = 5,
    Muted = 6,
}
