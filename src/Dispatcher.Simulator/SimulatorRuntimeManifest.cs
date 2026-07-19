using System.Text.Json;
using Dispatcher.Configuration;
using Dispatcher.Core;
using Dispatcher.Facilities;
using Dispatcher.Semantics;

namespace Dispatcher.Simulator;

public enum SimulatorManifestStatus
{
    Received = 1,
    Validated = 2,
    Rejected = 3,
    Activated = 4,
}

public sealed record SimulatorManifestDelivery(
    ConfigurationRevisionId RevisionId,
    FacilityScopeId ScopeId,
    RevisionNumber RevisionNumber,
    ConfigurationRevisionId? SourceRevisionId,
    string ManifestJson,
    string ManifestFingerprint,
    string DependencyFingerprint);

public sealed record SimulatorManifestReceipt(
    ConfigurationRevisionId RevisionId,
    FacilityScopeId ScopeId,
    RevisionNumber RevisionNumber,
    ConfigurationRevisionId? SourceRevisionId,
    string ManifestFingerprint,
    string DependencyFingerprint,
    SimulatorManifestStatus Status,
    string? RejectionCode,
    string? RejectionMessage,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset? ActivatedAt);

public sealed record ActiveSimulatorManifest(
    SimulatorManifestReceipt Receipt,
    SimulatorScenarioConfig Configuration,
    long Generation);

public sealed class SimulatorActivationCommitHook
{
    private readonly Func<CancellationToken, Task> callback;

    public SimulatorActivationCommitHook(Func<CancellationToken, Task> callback)
    {
        this.callback = callback;
    }

    public Task BeforeCommitAsync(CancellationToken cancellationToken) => callback(cancellationToken);
}

internal static class SimulatorManifestValidator
{
    public static Result<SimulatorScenarioConfig> Validate(
        FacilityScopeId scopeId,
        string manifestJson,
        string expectedFingerprint)
    {
        try
        {
            var normalized = ConfigurationManifestFingerprint.Normalize(manifestJson);
            if (!string.Equals(normalized.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
            {
                return Failure("simulator.manifest_fingerprint", "Simulator manifest fingerprint does not match its content.");
            }

            using var document = JsonDocument.Parse(normalized.Json);
            var simulator = document.RootElement.GetProperty("simulator");
            var sourceId = SourceId.From(simulator.GetProperty("sourceId").GetGuid());
            var seed = simulator.GetProperty("seed").GetUInt64();
            var points = simulator.GetProperty("points").EnumerateArray()
                .Select(point => new SimulatorPointConfig(
                    PointId.From(point.GetProperty("pointId").GetGuid()),
                    point.GetProperty("baseline").GetInt64(),
                    point.GetProperty("amplitude").GetInt64(),
                    Unit.FromSymbol(point.GetProperty("unit").GetString()!)))
                .ToArray();
            return Result.Success(new SimulatorScenarioConfig(RuntimeScopeId.From(scopeId.Value), sourceId, seed, points));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or InvalidOperationException or OverflowException)
        {
            return Failure("simulator.manifest_invalid", exception.Message);
        }
    }

    private static Result<SimulatorScenarioConfig> Failure(string code, string message) =>
        Result.Failure<SimulatorScenarioConfig>(new OperationError(ErrorCode.From(code), message));
}
