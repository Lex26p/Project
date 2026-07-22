using Dispatcher.Platform;

namespace Dispatcher.Administration;

public enum OperationalHealthState { Healthy = 1, Degraded = 2, Unavailable = 3, Unknown = 4 }
public enum OperationalHealthFacet { Persistence = 1, Recovery = 2, ProtectedContinuity = 3, Admission = 4, Capacity = 5, Connectivity = 6 }
public enum DataQualityIssueState { Open = 1, Resolved = 2 }
public enum DataQualityIssueKind { BadQuality = 1, Stale = 2, SourceGap = 3, Configuration = 4, Connectivity = 5 }
public enum OperationalSeverity { Information = 1, Warning = 2, Critical = 3 }
public enum AuditOutcome { Succeeded = 1, Rejected = 2, Unknown = 3 }
public enum AuditFeedKind { Records = 1, Gap = 2 }

public sealed record OwnerHealthFact(
    Guid FactId, string Owner, Guid? ScopeId, PermissionCode RequiredPermission,
    OperationalHealthFacet Facet, OperationalHealthState State, string ReasonCode,
    DateTimeOffset ObservedAt);
public sealed record OperationalHealthItem(
    string Owner, Guid? ScopeId, OperationalHealthFacet Facet,
    OperationalHealthState State, string ReasonCode, DateTimeOffset ObservedAt);
public sealed record OperationalHealthView(
    IReadOnlyList<OperationalHealthItem> Items, int Healthy, int Degraded, int Unavailable, int Unknown);

public sealed record OwnerDataQualityFact(
    Guid FactId, Guid IssueId, string Owner, Guid? ScopeId, PermissionCode RequiredPermission,
    DataQualityIssueKind Kind, OperationalSeverity Severity, DataQualityIssueState State,
    string Summary, string SourceReference, DateTimeOffset ObservedAt);
public sealed record DataQualityIssueItem(
    Guid IssueId, string Owner, Guid? ScopeId, DataQualityIssueKind Kind,
    OperationalSeverity Severity, string Summary, string SourceReference, DateTimeOffset ObservedAt);
public sealed record DataQualityView(IReadOnlyList<DataQualityIssueItem> Issues, int Warning, int Critical);

public sealed record OwnerAuditFact(
    Guid EventId, string Owner, Guid? ScopeId, PermissionCode RequiredPermission,
    SessionId? SessionId, SubjectId? SubjectId, PrincipalKind? PrincipalKind,
    string Action, string TargetKind, Guid? TargetId, AuditOutcome Outcome,
    string Summary, DateTimeOffset OccurredAt);
public sealed record AuditRecordItem(
    Guid EventId, string Owner, Guid? ScopeId, Guid? SubjectId, PrincipalKind? PrincipalKind,
    string Action, string TargetKind, Guid? TargetId, AuditOutcome Outcome,
    string Summary, DateTimeOffset OccurredAt);
public sealed record AuditQuery(string? Cursor, int PageSize);
public sealed record AuditFeedPage(AuditFeedKind Kind, IReadOnlyList<AuditRecordItem> Records, string? Cursor);

public sealed record AdministrationQueryLimits
{
    public AdministrationQueryLimits(int maximumViewItems, int maximumAuditPageSize, int retainedAuditTail)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumViewItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAuditPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedAuditTail);
        MaximumViewItems = maximumViewItems;
        MaximumAuditPageSize = maximumAuditPageSize;
        RetainedAuditTail = retainedAuditTail;
    }
    public int MaximumViewItems { get; }
    public int MaximumAuditPageSize { get; }
    public int RetainedAuditTail { get; }
}

public static class AdministrationPermissions
{
    public static readonly PermissionCode ReadHealth = PermissionCode.From("administration.health.read");
    public static readonly PermissionCode ReadDataQuality = PermissionCode.From("administration.data-quality.read");
    public static readonly PermissionCode ReadAudit = PermissionCode.From("administration.audit.read");
}
