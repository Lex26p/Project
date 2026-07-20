using Dispatcher.Core;
using Dispatcher.Semantics;

namespace Dispatcher.Protocols;

public sealed record ProtocolIoLimits
{
    public ProtocolIoLimits(
        TimeSpan timeout,
        int maxResponseBytes,
        int maxObservations,
        int maxConcurrentOperations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResponseBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxObservations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentOperations);
        Timeout = timeout;
        MaxResponseBytes = maxResponseBytes;
        MaxObservations = maxObservations;
        MaxConcurrentOperations = maxConcurrentOperations;
    }

    public TimeSpan Timeout { get; }
    public int MaxResponseBytes { get; }
    public int MaxObservations { get; }
    public int MaxConcurrentOperations { get; }
}

public enum ProtocolReadPurpose
{
    Acquisition = 1,
    Diagnostic = 2,
}

public sealed record ProtocolTransportRequest(
    ProtocolReadPurpose Purpose,
    SourceBinding Binding,
    ulong? ScheduleSequence,
    ProtocolWorkloadIdentity WorkloadIdentity,
    ProtocolSecretLease? Secret,
    int MaxResponseBytes);

public interface IReadOnlyProtocolTransport
{
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(
        ProtocolTransportRequest request,
        CancellationToken cancellationToken);
}

public interface IProtocolObservationParser
{
    Result<IReadOnlyList<SourceObservation>> Parse(SourceBinding binding, ReadOnlyMemory<byte> response);
}

public sealed record ProtocolSourceRequest(
    SourceBinding Binding,
    ulong ScheduleSequence,
    ProtocolSecretReference? SecretReference);

public sealed record ProtocolDiagnosticRequest(
    SourceBinding Binding,
    ProtocolSecretReference? SecretReference);

public enum ProtocolDiagnosticStatus
{
    Reachable = 1,
    Rejected = 2,
}

public sealed record ProtocolDiagnosticResult(ProtocolDiagnosticStatus Status, int ResponseBytes, string Code);

public sealed class ProtocolSourceController : IDisposable
{
    private readonly IReadOnlyProtocolTransport transport;
    private readonly IProtocolObservationParser parser;
    private readonly IProtocolSecretResolver secretResolver;
    private readonly ProtocolIoLimits limits;
    private readonly SemaphoreSlim operations;

    public ProtocolSourceController(
        ProtocolWorkloadIdentity workloadIdentity,
        IReadOnlyProtocolTransport transport,
        IProtocolObservationParser parser,
        IProtocolSecretResolver secretResolver,
        ProtocolIoLimits limits)
    {
        _ = workloadIdentity.Value ?? throw new ArgumentException("Workload identity must be defined.", nameof(workloadIdentity));
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(secretResolver);
        ArgumentNullException.ThrowIfNull(limits);
        WorkloadIdentity = workloadIdentity;
        this.transport = transport;
        this.parser = parser;
        this.secretResolver = secretResolver;
        this.limits = limits;
        operations = new SemaphoreSlim(limits.MaxConcurrentOperations, limits.MaxConcurrentOperations);
    }

    public ProtocolWorkloadIdentity WorkloadIdentity { get; }

    public async Task<Result<RuntimeCut>> AcquireAsync(
        ProtocolSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfZero(request.ScheduleSequence);
        var response = await ReadBoundedAsync(
            ProtocolReadPurpose.Acquisition,
            request.Binding,
            request.ScheduleSequence,
            request.SecretReference,
            cancellationToken).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return Result.Failure<RuntimeCut>(response.Error!);
        }

        Result<IReadOnlyList<SourceObservation>> parsed;
        try
        {
            parsed = parser.Parse(request.Binding, response.Value);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return Failure<RuntimeCut>("protocol.parse_failed", "The protocol response parser rejected hostile or malformed input.");
        }

        if (parsed.IsFailure)
        {
            return Result.Failure<RuntimeCut>(parsed.Error!);
        }

        if (parsed.Value.Count > limits.MaxObservations)
        {
            return Failure<RuntimeCut>("protocol.observation_limit", "The protocol response exceeds the observation limit.");
        }

        return RuntimeCut.Normalize(request.Binding, request.ScheduleSequence, parsed.Value);
    }

    public async Task<Result<ProtocolDiagnosticResult>> DiagnoseAsync(
        ProtocolDiagnosticRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await ReadBoundedAsync(
            ProtocolReadPurpose.Diagnostic,
            request.Binding,
            null,
            request.SecretReference,
            cancellationToken).ConfigureAwait(false);
        return response.IsSuccess
            ? Result.Success(new ProtocolDiagnosticResult(
                ProtocolDiagnosticStatus.Reachable,
                response.Value.Length,
                "protocol.diagnostic_reachable"))
            : Result.Failure<ProtocolDiagnosticResult>(response.Error!);
    }

    public void Dispose() => operations.Dispose();

    private async Task<Result<ReadOnlyMemory<byte>>> ReadBoundedAsync(
        ProtocolReadPurpose purpose,
        SourceBinding binding,
        ulong? scheduleSequence,
        ProtocolSecretReference? secretReference,
        CancellationToken cancellationToken)
    {
        if (!await operations.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return Failure<ReadOnlyMemory<byte>>(
                "protocol.io_capacity",
                "The bounded protocol I/O capacity is exhausted.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(limits.Timeout);
            using var secret = secretReference is null
                ? null
                : await secretResolver.ResolveAsync(
                    secretReference.Value,
                    WorkloadIdentity,
                    timeout.Token).ConfigureAwait(false);
            ReadOnlyMemory<byte> response;
            try
            {
                response = await transport.ReadAsync(
                    new ProtocolTransportRequest(
                        purpose,
                        binding,
                        scheduleSequence,
                        WorkloadIdentity,
                        secret,
                        limits.MaxResponseBytes),
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failure<ReadOnlyMemory<byte>>(
                    "protocol.io_timeout",
                    "The bounded protocol I/O deadline elapsed.");
            }

            if (response.Length > limits.MaxResponseBytes)
            {
                return Failure<ReadOnlyMemory<byte>>(
                    "protocol.response_limit",
                    "The protocol response exceeds the configured byte limit.");
            }

            return Result.Success(response);
        }
        finally
        {
            operations.Release();
        }
    }

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));
}
