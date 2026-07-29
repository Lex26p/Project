using System.Security.Cryptography;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Equipment;
using Dispatcher.Facilities;
using Dispatcher.Modbus;
using Dispatcher.ProtocolCommissioning;
using Dispatcher.Protocols;
using Dispatcher.Semantics;
using Dispatcher.Snmp;

namespace Dispatcher.RuntimeHost;

public sealed class EquipmentDiagnosticWorker
{
    private const int MaximumAttempts = 3;
    private readonly EquipmentStagingStore store;
    private readonly FacilityScopeId scopeId;
    private readonly string workerId;
    private readonly TimeSpan leaseDuration;
    private readonly TimeSpan pollInterval;
    private readonly ProtocolCommissioningLimits configurationLimits;
    private readonly SnmpWireLimits snmpWireLimits;
    private readonly ProtocolWorkloadIdentity workloadIdentity;
    private readonly ProtocolIoLimits ioLimits;
    private readonly IProtocolSecretResolver secretResolver;
    private readonly IModbusTcpConnectionFactory modbusFactory;
    private readonly ISnmpDatagramClientFactory snmpFactory;
    private readonly IWallClock clock;

    public EquipmentDiagnosticWorker(
        EquipmentStagingStore store,
        FacilityScopeId scopeId,
        string workerId,
        TimeSpan leaseDuration,
        TimeSpan pollInterval,
        ProtocolCommissioningLimits configurationLimits,
        SnmpWireLimits snmpWireLimits,
        ProtocolWorkloadIdentity workloadIdentity,
        ProtocolIoLimits ioLimits,
        IProtocolSecretResolver secretResolver,
        IModbusTcpConnectionFactory modbusFactory,
        ISnmpDatagramClientFactory snmpFactory,
        IWallClock clock)
    {
        this.store = store;
        this.scopeId = scopeId;
        this.workerId = workerId;
        this.leaseDuration = leaseDuration;
        this.pollInterval = pollInterval;
        this.configurationLimits = configurationLimits;
        this.snmpWireLimits = snmpWireLimits;
        this.workloadIdentity = workloadIdentity;
        this.ioLimits = ioLimits;
        this.secretResolver = secretResolver;
        this.modbusFactory = modbusFactory;
        this.snmpFactory = snmpFactory;
        this.clock = clock;
    }

    public async Task<Result> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var claimed = await store.ClaimDiagnosticAsync(
                    scopeId, workerId, leaseDuration, cancellationToken).ConfigureAwait(false);
                if (claimed.IsFailure)
                {
                    if (claimed.Error?.Code.Value == "diagnostic.job_not_available")
                    {
                        await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return Result.Failure(claimed.Error!);
                }

                await ExecuteAsync(claimed.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result.Success();
        }
    }

    private async Task ExecuteAsync(
        EquipmentDiagnosticJobClaim claim,
        CancellationToken cancellationToken)
    {
        if (claim.Attempts > MaximumAttempts)
        {
            _ = await store.CompleteDiagnosticAsync(
                claim,
                EquipmentDiagnosticJobStatus.Failed,
                "diagnostic.attempt_limit",
                "Diagnostic attempt limit was reached.",
                [],
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var plan = CreatePlan(claim);
        if (plan.IsFailure)
        {
            await CompleteFailureAsync(claim, plan.Error!, cancellationToken).ConfigureAwait(false);
            return;
        }

        var bindings = plan.Value.CreateBindings(SourceSessionGeneration.From((ulong)claim.Attempts));
        var modbusResult = ModbusRuntimeSourceFactory.Create(
            plan.Value,
            bindings.Where(binding =>
                plan.Value.ModbusSources.Any(source => source.SourceId == binding.SourceId)).ToArray(),
            configurationLimits.Modbus,
            workloadIdentity,
            ioLimits,
            modbusFactory,
            clock);
        if (modbusResult.IsFailure)
        {
            await CompleteFailureAsync(claim, modbusResult.Error!, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var modbus = modbusResult.Value;
        var snmpResult = SnmpRuntimeSourceFactory.Create(
            plan.Value,
            bindings.Where(binding =>
                plan.Value.SnmpSources.Any(source => source.SourceId == binding.SourceId)).ToArray(),
            configurationLimits.Snmp,
            snmpWireLimits,
            workloadIdentity,
            ioLimits,
            secretResolver,
            snmpFactory,
            clock);
        if (snmpResult.IsFailure)
        {
            await CompleteFailureAsync(claim, snmpResult.Error!, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var snmp = snmpResult.Value;

        Result<ProtocolDiagnosticResult> result;
        if (modbus.Sources.Count == 1)
        {
            var source = modbus.Sources[0];
            result = await source.Controller.DiagnoseAsync(
                new ProtocolDiagnosticRequest(
                    source.Binding,
                    source.SecretReference,
                    Mode(claim.Mode)),
                cancellationToken).ConfigureAwait(false);
        }
        else if (snmp.Sources.Count == 1)
        {
            var source = snmp.Sources[0];
            result = await source.Controller.DiagnoseAsync(
                new ProtocolDiagnosticRequest(
                    source.Binding,
                    source.SecretReference,
                    Mode(claim.Mode)),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await CompleteFailureAsync(
                claim,
                new OperationError(
                    ErrorCode.From("diagnostic.source_count"),
                    "Diagnostic manifest must contain exactly one source."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (result.IsFailure)
        {
            await CompleteFailureAsync(claim, result.Error!, cancellationToken).ConfigureAwait(false);
            return;
        }

        var samples = result.Value.Samples.Select(sample => new EquipmentDiagnosticSample(
            sample.PointId.Value.ToString("D"),
            sample.Value,
            sample.Unit.Symbol,
            sample.Quality.ToString(),
            clock.GetUtcNow(),
            string.Equals(sample.Code, "protocol.sample", StringComparison.Ordinal)
                ? null
                : sample.Code)).ToArray();
        _ = await store.CompleteDiagnosticAsync(
            claim,
            EquipmentDiagnosticJobStatus.Succeeded,
            "diagnostic.succeeded",
            "Diagnostic completed.",
            samples,
            cancellationToken).ConfigureAwait(false);
    }

    private Result<ProtocolActivationPlan> CreatePlan(EquipmentDiagnosticJobClaim claim)
    {
        try
        {
            var normalized = ConfigurationManifestFingerprint.Normalize(claim.ManifestJson);
            var now = clock.GetUtcNow();
            var revision = new ConfigurationRevisionSnapshot(
                ConfigurationRevisionId.From(claim.JobId),
                claim.ScopeId,
                RevisionNumber.Initial,
                null,
                normalized.Json,
                normalized.Fingerprint,
                [],
                normalized.Fingerprint,
                1,
                now,
                now,
                now,
                now,
                null);
            return ProtocolCommissioningManifest.CreatePlan(revision, configurationLimits);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or FormatException)
        {
            return Result.Failure<ProtocolActivationPlan>(
                new OperationError(
                    ErrorCode.From("diagnostic.manifest_invalid"),
                    "Diagnostic manifest is invalid."));
        }
    }

    private async Task CompleteFailureAsync(
        EquipmentDiagnosticJobClaim claim,
        OperationError error,
        CancellationToken cancellationToken)
    {
        var status = error.Code.Value.Contains("timeout", StringComparison.Ordinal)
            ? EquipmentDiagnosticJobStatus.TimedOut
            : EquipmentDiagnosticJobStatus.Failed;
        _ = await store.CompleteDiagnosticAsync(
            claim,
            status,
            error.Code.Value,
            error.Message,
            [],
            cancellationToken).ConfigureAwait(false);
    }

    private static ProtocolDiagnosticMode Mode(EquipmentDiagnosticMode mode) =>
        mode == EquipmentDiagnosticMode.ConnectionTest
            ? ProtocolDiagnosticMode.ConnectionTest
            : ProtocolDiagnosticMode.SamplePoll;
}

public sealed class DatabaseProtocolSecretResolver : IProtocolSecretResolver
{
    private readonly EquipmentStagingStore store;
    private readonly FacilityScopeId scopeId;
    private readonly StagingSecretProtector protector;
    private readonly ProtocolWorkloadIdentity workloadIdentity;

    public DatabaseProtocolSecretResolver(
        EquipmentStagingStore store,
        FacilityScopeId scopeId,
        StagingSecretProtector protector,
        ProtocolWorkloadIdentity workloadIdentity)
    {
        this.store = store;
        this.scopeId = scopeId;
        this.protector = protector;
        this.workloadIdentity = workloadIdentity;
    }

    public async ValueTask<ProtocolSecretLease> ResolveAsync(
        ProtocolSecretReference reference,
        ProtocolWorkloadIdentity workloadIdentity,
        CancellationToken cancellationToken)
    {
        if (workloadIdentity != this.workloadIdentity)
        {
            throw new UnauthorizedAccessException(
                "The runtime workload cannot resolve this secret reference.");
        }

        var resolved = await store.ResolveProtocolSecretAsync(
            scopeId, reference.Value, protector, cancellationToken).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            throw new InvalidOperationException("The referenced runtime secret is unavailable.");
        }

        var characters = resolved.Value;
        try
        {
            return ProtocolSecretLease.Create(characters);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(characters.AsSpan()));
        }
    }
}

internal sealed class CompositeProtocolSecretResolver : IProtocolSecretResolver
{
    private readonly IProtocolSecretResolver environment;
    private readonly IProtocolSecretResolver database;

    public CompositeProtocolSecretResolver(
        IProtocolSecretResolver environment,
        IProtocolSecretResolver database)
    {
        this.environment = environment;
        this.database = database;
    }

    public ValueTask<ProtocolSecretLease> ResolveAsync(
        ProtocolSecretReference reference,
        ProtocolWorkloadIdentity workloadIdentity,
        CancellationToken cancellationToken) =>
        reference.Value.StartsWith("db:", StringComparison.Ordinal)
            ? database.ResolveAsync(reference, workloadIdentity, cancellationToken)
            : environment.ResolveAsync(reference, workloadIdentity, cancellationToken);
}
