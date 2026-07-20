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

public enum ProtocolDiagnosticMode
{
    ConnectionTest = 1,
    SamplePoll = 2,
}

public sealed record ProtocolTransportRequest(
    ProtocolReadPurpose Purpose,
    SourceBinding Binding,
    ulong? ScheduleSequence,
    ProtocolWorkloadIdentity WorkloadIdentity,
    ProtocolSecretLease? Secret,
    int MaxResponseBytes,
    ProtocolDiagnosticMode? DiagnosticMode);

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

public sealed record ProtocolDiagnosticSample(
    PointId PointId,
    long Value,
    Unit Unit,
    DataQuality Quality,
    string Code);

public sealed record ProtocolDiagnosticBatch(
    IReadOnlyList<ProtocolDiagnosticSample> Samples,
    bool Partial);

public interface IProtocolDiagnosticParser
{
    Result<ProtocolDiagnosticBatch> ParseDiagnostic(ReadOnlyMemory<byte> response);
}

public sealed record ProtocolSourceRequest(
    SourceBinding Binding,
    ulong ScheduleSequence,
    ProtocolSecretReference? SecretReference);

public sealed record ProtocolDiagnosticRequest(
    SourceBinding Binding,
    ProtocolSecretReference? SecretReference,
    ProtocolDiagnosticMode Mode = ProtocolDiagnosticMode.SamplePoll);

public enum ProtocolDiagnosticStatus
{
    Reachable = 1,
    Rejected = 2,
}

public sealed record ProtocolDiagnosticResult(
    ProtocolDiagnosticStatus Status,
    int ResponseBytes,
    string Code,
    IReadOnlyList<ProtocolDiagnosticSample> Samples,
    bool Partial);

public sealed class ProtocolSourceController : IDisposable
{
    private readonly IReadOnlyProtocolTransport transport;
    private readonly IProtocolObservationParser parser;
    private readonly IProtocolSecretResolver secretResolver;
    private readonly IProtocolDiagnosticParser? diagnosticParser;
    private readonly ProtocolIoLimits limits;
    private readonly SemaphoreSlim operations;

    public ProtocolSourceController(
        ProtocolWorkloadIdentity workloadIdentity,
        IReadOnlyProtocolTransport transport,
        IProtocolObservationParser parser,
        IProtocolSecretResolver secretResolver,
        ProtocolIoLimits limits,
        IProtocolDiagnosticParser? diagnosticParser = null)
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
        this.diagnosticParser = diagnosticParser;
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
            null,
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
            request.Mode,
            cancellationToken).ConfigureAwait(false);
        if (response.IsFailure)
        {
            return Result.Failure<ProtocolDiagnosticResult>(response.Error!);
        }

        if (request.Mode == ProtocolDiagnosticMode.ConnectionTest || diagnosticParser is null)
        {
            return Result.Success(new ProtocolDiagnosticResult(
                ProtocolDiagnosticStatus.Reachable,
                response.Value.Length,
                "protocol.diagnostic_reachable",
                [],
                false));
        }

        Result<ProtocolDiagnosticBatch> parsed;
        try
        {
            parsed = diagnosticParser.ParseDiagnostic(response.Value);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return Failure<ProtocolDiagnosticResult>(
                "protocol.diagnostic_parse_failed",
                "The diagnostic parser rejected hostile or malformed input.");
        }

        if (parsed.IsFailure)
        {
            return Result.Failure<ProtocolDiagnosticResult>(parsed.Error!);
        }

        if (parsed.Value.Samples.Count > limits.MaxObservations)
        {
            return Failure<ProtocolDiagnosticResult>(
                "protocol.observation_limit",
                "The diagnostic response exceeds the observation limit.");
        }

        return Result.Success(new ProtocolDiagnosticResult(
            ProtocolDiagnosticStatus.Reachable,
            response.Value.Length,
            parsed.Value.Partial ? "protocol.diagnostic_partial" : "protocol.diagnostic_reachable",
            parsed.Value.Samples,
            parsed.Value.Partial));
    }

    public void Dispose() => operations.Dispose();

    private async Task<Result<ReadOnlyMemory<byte>>> ReadBoundedAsync(
        ProtocolReadPurpose purpose,
        SourceBinding binding,
        ulong? scheduleSequence,
        ProtocolSecretReference? secretReference,
        ProtocolDiagnosticMode? diagnosticMode,
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
            ProtocolSecretLease? resolvedSecret;
            try
            {
                resolvedSecret = secretReference is null
                    ? null
                    : await secretResolver.ResolveAsync(
                        secretReference.Value,
                        WorkloadIdentity,
                        timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failure<ReadOnlyMemory<byte>>(
                    "protocol.io_timeout",
                    "The bounded protocol I/O deadline elapsed.");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return Failure<ReadOnlyMemory<byte>>(
                    "protocol.secret_unavailable",
                    "The protocol secret reference could not be resolved for this workload.");
            }

            using var secret = resolvedSecret;
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
                        limits.MaxResponseBytes,
                        diagnosticMode),
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failure<ReadOnlyMemory<byte>>(
                    "protocol.io_timeout",
                    "The bounded protocol I/O deadline elapsed.");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                return Failure<ReadOnlyMemory<byte>>(
                    "protocol.io_failed",
                    "The bounded protocol I/O operation failed.");
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
