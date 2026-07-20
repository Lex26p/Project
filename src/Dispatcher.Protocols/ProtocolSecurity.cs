using System.Security.Cryptography;

namespace Dispatcher.Protocols;

public readonly record struct ProtocolWorkloadIdentity
{
    private ProtocolWorkloadIdentity(string value) => Value = value;

    public string Value { get; }

    public static ProtocolWorkloadIdentity From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not ':'))
        {
            throw new ArgumentException("A workload identity must be a bounded machine identity.", nameof(value));
        }

        return new ProtocolWorkloadIdentity(value);
    }

    public override string ToString() => Value;
}

public readonly record struct ProtocolSecretReference
{
    private ProtocolSecretReference(string value) => Value = value;

    public string Value { get; }

    public static ProtocolSecretReference From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.' and not ':'))
        {
            throw new ArgumentException("A secret reference must be bounded and contain no secret material.", nameof(value));
        }

        return new ProtocolSecretReference(value);
    }

    public override string ToString() => Value;
}

public sealed class ProtocolSecretLease : IDisposable
{
    private char[]? value;

    private ProtocolSecretLease(char[] value) => this.value = value;

    public static ProtocolSecretLease Create(string rawSecret)
    {
        ArgumentException.ThrowIfNullOrEmpty(rawSecret);
        return new ProtocolSecretLease(rawSecret.ToCharArray());
    }

    public ValueTask<TResult> UseAsync<TResult>(
        Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var current = value ?? throw new ObjectDisposedException(nameof(ProtocolSecretLease));
        return operation(current, cancellationToken);
    }

    public override string ToString() => "[REDACTED]";

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref value, null);
        if (current is not null)
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(current.AsSpan()));
        }
    }
}

public interface IProtocolSecretResolver
{
    ValueTask<ProtocolSecretLease> ResolveAsync(
        ProtocolSecretReference reference,
        ProtocolWorkloadIdentity workloadIdentity,
        CancellationToken cancellationToken);
}
