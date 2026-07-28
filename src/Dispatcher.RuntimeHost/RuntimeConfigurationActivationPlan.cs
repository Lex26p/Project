using System.Text.Json;
using Dispatcher.Alarm;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.ProtocolCommissioning;
using Dispatcher.Semantics;
using Dispatcher.Simulator;

namespace Dispatcher.RuntimeHost;

public sealed record RuntimeConfigurationActivationPlan(
    ConfigurationWorkloadClaim Claim,
    SimulatorManifestDelivery Simulator,
    IReadOnlyList<AlarmDefinition> AlarmDefinitions,
    ProtocolActivationPlan? ProtocolExtension);

public static class RuntimeConfigurationActivationPlanFactory
{
    private static readonly ProtocolCommissioningLimits ProtocolLimits = new(
        new(256, 512),
        new(256, 128, 512));

    public static Result<RuntimeConfigurationActivationPlan> Create(
        ConfigurationWorkloadClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        try
        {
            var revision = claim.Revision;
            if (revision.PublishedAt is null)
            {
                return Failure(
                    "runtime.configuration_not_published",
                    "The workload deployment revision is not published.");
            }

            var normalized = ConfigurationManifestFingerprint.Normalize(revision.ManifestJson);
            var releaseFingerprint = ConfigurationManifestFingerprint.CombineRelease(
                revision.ManifestFingerprint,
                revision.DependencyFingerprint);
            if (!string.Equals(normalized.Fingerprint, revision.ManifestFingerprint, StringComparison.Ordinal) ||
                !string.Equals(releaseFingerprint, claim.ReleaseFingerprint, StringComparison.Ordinal))
            {
                return Failure(
                    "runtime.configuration_fingerprint",
                    "The workload deployment fingerprint does not match immutable content.");
            }

            using var document = JsonDocument.Parse(normalized.Json);
            _ = document.RootElement.GetProperty("simulator");
            var alarms = ParseAlarmDefinitions(document.RootElement);
            ProtocolActivationPlan? protocolExtension = null;
            if (document.RootElement.TryGetProperty("protocolSources", out var protocolSources) &&
                protocolSources.ValueKind == JsonValueKind.Array &&
                protocolSources.GetArrayLength() > 0)
            {
                var protocol = ProtocolCommissioningManifest.CreatePlan(
                    revision with { DistributedAt = revision.PublishedAt },
                    ProtocolLimits);
                if (protocol.IsFailure)
                {
                    return Result.Failure<RuntimeConfigurationActivationPlan>(protocol.Error!);
                }

                protocolExtension = protocol.Value;
            }

            return Result.Success(new RuntimeConfigurationActivationPlan(
                claim,
                new SimulatorManifestDelivery(
                    revision.RevisionId,
                    revision.ScopeId,
                    revision.RevisionNumber,
                    revision.SourceRevisionId,
                    normalized.Json,
                    revision.ManifestFingerprint,
                    revision.DependencyFingerprint),
                alarms,
                protocolExtension));
        }
        catch (Exception exception) when (
            exception is JsonException or KeyNotFoundException or InvalidOperationException or
                ArgumentException or FormatException or OverflowException)
        {
            return Failure(
                "runtime.configuration_manifest_invalid",
                "The whole-scope runtime manifest structure is invalid.");
        }
    }

    private static AlarmDefinition[] ParseAlarmDefinitions(JsonElement root)
    {
        if (!root.TryGetProperty("alarmDefinitions", out var definitions))
        {
            return [];
        }

        if (definitions.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Alarm definitions must be an array.");
        }

        return definitions.EnumerateArray()
            .Select(definition => new AlarmDefinition(
                AlarmDefinitionId.From(definition.GetProperty("definitionId").GetGuid()),
                PointId.From(definition.GetProperty("pointId").GetGuid()),
                definition.GetProperty("name").GetString()!,
                definition.GetProperty("direction").GetString() switch
                {
                    "high" => AlarmThresholdDirection.High,
                    "low" => AlarmThresholdDirection.Low,
                    _ => throw new FormatException("Unsupported alarm threshold direction."),
                },
                definition.GetProperty("threshold").GetInt64(),
                definition.GetProperty("hysteresis").GetInt64(),
                TimeSpan.FromMilliseconds(definition.GetProperty("raiseDelayMs").GetInt64()),
                TimeSpan.FromMilliseconds(definition.GetProperty("clearDelayMs").GetInt64()),
                !definition.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean(),
                !definition.TryGetProperty("priority", out var priority)
                    ? AlarmPriority.Medium
                    : priority.GetString() switch
                    {
                        "low" => AlarmPriority.Low,
                        "medium" => AlarmPriority.Medium,
                        "high" => AlarmPriority.High,
                        "critical" => AlarmPriority.Critical,
                        _ => throw new FormatException("Unsupported alarm priority."),
                    }))
            .ToArray();
    }

    private static Result<RuntimeConfigurationActivationPlan> Failure(string code, string message) =>
        Result.Failure<RuntimeConfigurationActivationPlan>(
            new OperationError(ErrorCode.From(code), message));
}
