using System.Text.Json;
using Dispatcher.Notifications;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class SmtpNotificationProviderTests
{
    private const string RawSecret = "smtp-password-value";

    [Fact]
    public void ProductionProfileRequiresTlsAndReferenceOnlyCredential()
    {
        Assert.Throws<ArgumentException>(() => new SmtpProviderConfiguration(
            SmtpProviderProfile.Production,
            "smtp.example",
            587,
            false,
            "dispatcher@example.test",
            "dispatcher",
            NotificationSecretReference.From("env:SMTP_PASSWORD"),
            TimeSpan.FromSeconds(10)));
        Assert.Throws<ArgumentException>(() => new SmtpProviderConfiguration(
            SmtpProviderProfile.Production,
            "smtp.example",
            587,
            true,
            "dispatcher@example.test",
            null,
            null,
            TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task SecretIsLeasedForProviderCallAndAbsentFromSerializableConfiguration()
    {
        var configuration = ProductionConfiguration();
        var client = new CapturingClient();
        var resolver = new StaticResolver();
        var provider = new SmtpNotificationProvider(configuration, resolver, client);

        var delivered = await provider.TestChannelAsync("operator@example.test");

        Assert.Equal(NotificationAttemptOutcome.Delivered, delivered.Outcome);
        Assert.Equal(RawSecret, client.ObservedSecret);
        Assert.Equal(1, resolver.Resolutions);
        var persisted = JsonSerializer.Serialize(configuration);
        Assert.Contains("env:SMTP_PASSWORD", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(RawSecret, persisted, StringComparison.Ordinal);
        using var lease = NotificationSecretLease.Create(RawSecret);
        Assert.Equal("[REDACTED]", lease.ToString());
    }

    [Fact]
    public async Task ProviderTimeoutIsTransientAndDoesNotInventDeliveryReceipt()
    {
        var provider = new SmtpNotificationProvider(
            ProductionConfiguration(),
            new StaticResolver(),
            new TimeoutClient());

        var result = await provider.TestChannelAsync("operator@example.test");

        Assert.Equal(NotificationAttemptOutcome.TransientFailure, result.Outcome);
        Assert.Equal("smtp.timeout", result.OutcomeCode);
        Assert.Null(result.ProviderReceipt);
    }

    private static SmtpProviderConfiguration ProductionConfiguration() => new(
        SmtpProviderProfile.Production,
        "smtp.example",
        587,
        true,
        "dispatcher@example.test",
        "dispatcher",
        NotificationSecretReference.From("env:SMTP_PASSWORD"),
        TimeSpan.FromSeconds(10));

    private sealed class StaticResolver : INotificationSecretResolver
    {
        public int Resolutions { get; private set; }

        public ValueTask<NotificationSecretLease> ResolveAsync(
            NotificationSecretReference reference,
            CancellationToken cancellationToken)
        {
            Resolutions++;
            return ValueTask.FromResult(NotificationSecretLease.Create(RawSecret));
        }
    }

    private sealed class CapturingClient : ISmtpClientAdapter
    {
        public string? ObservedSecret { get; private set; }

        public Task<string> SendAsync(
            SmtpProviderConfiguration configuration,
            SmtpDeliveryMessage message,
            ReadOnlyMemory<char>? credential,
            CancellationToken cancellationToken)
        {
            ObservedSecret = credential is null ? null : new string(credential.Value.Span);
            return Task.FromResult("controlled-receipt");
        }
    }

    private sealed class TimeoutClient : ISmtpClientAdapter
    {
        public Task<string> SendAsync(
            SmtpProviderConfiguration configuration,
            SmtpDeliveryMessage message,
            ReadOnlyMemory<char>? credential,
            CancellationToken cancellationToken) =>
            Task.FromException<string>(new OperationCanceledException("provider timeout"));
    }
}
