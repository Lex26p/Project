using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;

namespace Dispatcher.Notifications;

public enum SmtpProviderProfile
{
    Production = 1,
    ControlledTest = 2,
}

public readonly record struct NotificationSecretReference
{
    private NotificationSecretReference(string value) => Value = value;
    public string Value { get; }

    public static NotificationSecretReference From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not ':'))
        {
            throw new ArgumentException("Notification secret reference is invalid.", nameof(value));
        }

        return new NotificationSecretReference(value);
    }

    public override string ToString() => Value;
}

public sealed class NotificationSecretLease : IDisposable
{
    private char[]? value;
    private NotificationSecretLease(char[] value) => this.value = value;
    public static NotificationSecretLease Create(string rawSecret) => new(rawSecret.ToCharArray());

    public async ValueTask<TResult> UseAsync<TResult>(
        Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken)
    {
        var current = value ?? throw new ObjectDisposedException(nameof(NotificationSecretLease));
        return await operation(current, cancellationToken).ConfigureAwait(false);
    }

    public override string ToString() => "[REDACTED]";

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref value, null);
        if (current is not null)
        {
            CryptographicOperations.ZeroMemory(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(current.AsSpan()));
        }
    }
}

public interface INotificationSecretResolver
{
    ValueTask<NotificationSecretLease> ResolveAsync(
        NotificationSecretReference reference,
        CancellationToken cancellationToken);
}

public sealed class EnvironmentNotificationSecretResolver : INotificationSecretResolver
{
    public ValueTask<NotificationSecretLease> ResolveAsync(
        NotificationSecretReference reference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string prefix = "env:";
        if (!reference.Value.StartsWith(prefix, StringComparison.Ordinal) ||
            reference.Value.Length == prefix.Length)
        {
            return ValueTask.FromException<NotificationSecretLease>(
                new UnauthorizedAccessException("Notification credential reference provider is unsupported."));
        }

        var value = Environment.GetEnvironmentVariable(reference.Value[prefix.Length..]);
        return string.IsNullOrEmpty(value)
            ? ValueTask.FromException<NotificationSecretLease>(
                new UnauthorizedAccessException("Notification credential reference cannot be resolved."))
            : ValueTask.FromResult(NotificationSecretLease.Create(value));
    }
}

public sealed record SmtpProviderConfiguration
{
    public SmtpProviderConfiguration(
        SmtpProviderProfile profile,
        string host,
        int port,
        bool tls,
        string senderAddress,
        string? userName,
        NotificationSecretReference? credentialReference,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderAddress);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (!MailAddress.TryCreate(senderAddress, out _) || host.Length > 253 || senderAddress.Length > 320 ||
            userName?.Length > 256 || profile == SmtpProviderProfile.Production &&
            (!tls || string.IsNullOrWhiteSpace(userName) || credentialReference is null) ||
            profile == SmtpProviderProfile.ControlledTest && credentialReference is not null && string.IsNullOrWhiteSpace(userName))
        {
            throw new ArgumentException("SMTP provider configuration is invalid.");
        }

        Profile = profile;
        Host = host;
        Port = port;
        Tls = tls;
        SenderAddress = senderAddress;
        UserName = userName;
        CredentialReference = credentialReference;
        Timeout = timeout;
    }

    public SmtpProviderProfile Profile { get; }
    public string Host { get; }
    public int Port { get; }
    public bool Tls { get; }
    public string SenderAddress { get; }
    public string? UserName { get; }
    public NotificationSecretReference? CredentialReference { get; }
    public TimeSpan Timeout { get; }
}

public sealed record SmtpDeliveryMessage(
    NotificationAttemptId AttemptId,
    string RecipientAddress,
    string Subject,
    string Body);

public interface ISmtpClientAdapter
{
    Task<string> SendAsync(
        SmtpProviderConfiguration configuration,
        SmtpDeliveryMessage message,
        ReadOnlyMemory<char>? credential,
        CancellationToken cancellationToken);
}

public sealed class SystemSmtpClientAdapter : ISmtpClientAdapter
{
    public async Task<string> SendAsync(
        SmtpProviderConfiguration configuration,
        SmtpDeliveryMessage message,
        ReadOnlyMemory<char>? credential,
        CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(configuration.Host, configuration.Port)
        {
            EnableSsl = configuration.Tls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Timeout = checked((int)Math.Min(configuration.Timeout.TotalMilliseconds, int.MaxValue)),
        };
        if (configuration.UserName is not null && credential is not null)
        {
            client.Credentials = new NetworkCredential(configuration.UserName, new string(credential.Value.Span));
        }

        using var mail = new MailMessage(configuration.SenderAddress, message.RecipientAddress)
        {
            Subject = message.Subject,
            Body = message.Body,
        };
        var messageId = $"<{message.AttemptId.Value:N}@dispatcher>";
        mail.Headers.Add("Message-ID", messageId);
        await client.SendMailAsync(mail, cancellationToken).ConfigureAwait(false);
        return messageId;
    }
}

public sealed class SmtpNotificationProvider
{
    private readonly SmtpProviderConfiguration configuration;
    private readonly INotificationSecretResolver secretResolver;
    private readonly ISmtpClientAdapter client;

    public SmtpNotificationProvider(
        SmtpProviderConfiguration configuration,
        INotificationSecretResolver secretResolver,
        ISmtpClientAdapter client)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.secretResolver = secretResolver ?? throw new ArgumentNullException(nameof(secretResolver));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<NotificationProviderResult> SendAsync(
        SmtpDeliveryMessage message,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (configuration.CredentialReference is { } reference)
            {
                using var lease = await secretResolver.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
                return await lease.UseAsync(
                    async (secret, token) => NotificationProviderResult.Delivered(
                        await client.SendAsync(configuration, message, secret, token).ConfigureAwait(false)),
                    cancellationToken).ConfigureAwait(false);
            }

            return NotificationProviderResult.Delivered(
                await client.SendAsync(configuration, message, null, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return NotificationProviderResult.Transient("smtp.timeout");
        }
        catch (SmtpFailedRecipientException)
        {
            return NotificationProviderResult.Permanent("smtp.recipient_rejected");
        }
        catch (Exception exception) when (exception is SmtpException or IOException)
        {
            return NotificationProviderResult.Transient("smtp.unavailable");
        }
    }

    public Task<NotificationProviderResult> TestChannelAsync(
        string recipientAddress,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            new SmtpDeliveryMessage(
                NotificationAttemptId.New(),
                recipientAddress,
                "Dispatcher SMTP channel test",
                "Dispatcher controlled channel test."),
            cancellationToken);
}
