using Dispatcher.Events;
using Dispatcher.Semantics;
using Dispatcher.Workspace;

namespace Dispatcher.Notifications;

public readonly record struct NotificationDeliveryId
{
    private readonly CanonicalId<NotificationDeliveryId> value;
    private NotificationDeliveryId(CanonicalId<NotificationDeliveryId> value) => this.value = value;
    public Guid Value => value.Value;
    public static NotificationDeliveryId New() => new(CanonicalId.New<NotificationDeliveryId>());
    public static NotificationDeliveryId From(Guid value) => new(CanonicalId.From<NotificationDeliveryId>(value));
}

public readonly record struct NotificationAttemptId
{
    private readonly CanonicalId<NotificationAttemptId> value;
    private NotificationAttemptId(CanonicalId<NotificationAttemptId> value) => this.value = value;
    public Guid Value => value.Value;
    public static NotificationAttemptId New() => new(CanonicalId.New<NotificationAttemptId>());
    public static NotificationAttemptId From(Guid value) => new(CanonicalId.From<NotificationAttemptId>(value));
}

public enum NotificationDeliveryStatus
{
    Pending = 1,
    InFlight = 2,
    PendingRetry = 3,
    Delivered = 4,
    EscalationRequired = 5,
    TerminalFailure = 6,
}

public enum NotificationAttemptOutcome
{
    InFlight = 1,
    Delivered = 2,
    TransientFailure = 3,
    PermanentFailure = 4,
}

public sealed record NotificationDeliveryPolicy
{
    public NotificationDeliveryPolicy(int maxAttempts, IReadOnlyList<TimeSpan> retryDelays)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAttempts, 10);
        ArgumentNullException.ThrowIfNull(retryDelays);
        if (retryDelays.Count != maxAttempts - 1 || retryDelays.Any(delay => delay < TimeSpan.Zero))
        {
            throw new ArgumentException("Retry delays must define every bounded retry.", nameof(retryDelays));
        }

        MaxAttempts = maxAttempts;
        RetryDelays = retryDelays.ToArray();
    }

    public int MaxAttempts { get; }
    public IReadOnlyList<TimeSpan> RetryDelays { get; }
    public TimeSpan DelayAfter(int failedAttempt) => RetryDelays[failedAttempt - 1];
}

public sealed record NotificationDeliveryObligation(
    NotificationDeliveryId DeliveryId,
    OperationalEventId EventId,
    PersonId RecipientId,
    PersonId? CoveredPersonId,
    bool Mandatory,
    string EmailAddress,
    string Subject,
    string Body,
    NotificationDeliveryStatus Status,
    int AttemptCount,
    DateTimeOffset NextAttemptAt,
    NotificationAttemptId? ActiveAttemptId,
    string? LeaseOwner,
    DateTimeOffset? LeaseUntil,
    StateVersion Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record NotificationProviderAttempt(
    NotificationAttemptId AttemptId,
    NotificationDeliveryId DeliveryId,
    int AttemptNumber,
    NotificationAttemptOutcome Outcome,
    string? OutcomeCode,
    string? ProviderReceipt,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record NotificationDeliveryClaim(
    NotificationDeliveryObligation Obligation,
    NotificationProviderAttempt Attempt);

public sealed record NotificationDeliveryClaimResult(NotificationDeliveryClaim? Claim);

public sealed record NotificationDeliveryRunResult(NotificationDeliveryObligation? Obligation);

public sealed record NotificationProviderResult(
    NotificationAttemptOutcome Outcome,
    string OutcomeCode,
    string? ProviderReceipt)
{
    public static NotificationProviderResult Delivered(string receipt) =>
        new(NotificationAttemptOutcome.Delivered, "smtp.delivered", receipt);

    public static NotificationProviderResult Transient(string code) =>
        new(NotificationAttemptOutcome.TransientFailure, code, null);

    public static NotificationProviderResult Permanent(string code) =>
        new(NotificationAttemptOutcome.PermanentFailure, code, null);
}
