using System.Globalization;
using Dispatcher.Core;
using Dispatcher.Protocols;
using Dispatcher.Semantics;

namespace Dispatcher.Snmp;

public enum SnmpRuntimeProfile
{
    NonProductionV2cReadOnly = 1,
}

public enum SnmpNumericType
{
    Signed32 = 1,
    Counter32 = 2,
    Gauge32 = 3,
    TimeTicks = 4,
    Counter64 = 5,
}

public readonly record struct SnmpOid
{
    private SnmpOid(string value) => Value = value;

    public string Value { get; }

    public static Result<SnmpOid> Parse(string value, int maxArcs, int maxEncodedBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxArcs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEncodedBytes);
        var text = value[0] == '.' ? value[1..] : value;
        var segments = text.Split('.', StringSplitOptions.None);
        if (segments.Length < 2 || segments.Length > maxArcs)
        {
            return Failure("snmp.oid_arcs", "SNMP OID arc count is invalid or exceeds the configured bound.");
        }

        var arcs = new uint[segments.Length];
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index].Length == 0 ||
                !uint.TryParse(segments[index], NumberStyles.None, CultureInfo.InvariantCulture, out arcs[index]))
            {
                return Failure("snmp.oid", "SNMP OID contains an invalid arc.");
            }
        }

        if (arcs[0] > 2 || arcs[0] < 2 && arcs[1] > 39)
        {
            return Failure("snmp.oid", "SNMP OID root arcs are invalid.");
        }

        var encodedBytes = Base128Length(checked((ulong)(arcs[0] * 40) + arcs[1]));
        for (var index = 2; index < arcs.Length; index++)
        {
            encodedBytes = checked(encodedBytes + Base128Length(arcs[index]));
        }

        return encodedBytes <= maxEncodedBytes
            ? Result.Success(new SnmpOid(string.Join('.', arcs)))
            : Failure("snmp.oid_size", "SNMP OID encoding exceeds the configured byte bound.");
    }

    internal uint[] GetArcs() => Value.Split('.').Select(segment =>
        uint.Parse(segment, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray();

    public override string ToString() => Value;

    private static int Base128Length(ulong value)
    {
        var length = 1;
        while ((value >>= 7) != 0)
        {
            length++;
        }

        return length;
    }

    private static Result<SnmpOid> Failure(string code, string message) =>
        Result.Failure<SnmpOid>(new OperationError(ErrorCode.From(code), message));
}

public sealed record SnmpPointConfiguration(
    PointId PointId,
    SnmpOid Oid,
    SnmpNumericType ValueType,
    Unit Unit);

public sealed record SnmpRetryPolicy
{
    public SnmpRetryPolicy(int maxAttempts, TimeSpan responseTimeout, TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAttempts, 10);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(responseTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        MaxAttempts = maxAttempts;
        ResponseTimeout = responseTimeout;
        Delay = delay;
    }

    public int MaxAttempts { get; }
    public TimeSpan ResponseTimeout { get; }
    public TimeSpan Delay { get; }
}

public sealed record SnmpConfigurationLimits
{
    public SnmpConfigurationLimits(int maxPoints, int maxOidArcs, int maxOidBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPoints);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOidArcs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOidBytes);
        MaxPoints = maxPoints;
        MaxOidArcs = maxOidArcs;
        MaxOidBytes = maxOidBytes;
    }

    public int MaxPoints { get; }
    public int MaxOidArcs { get; }
    public int MaxOidBytes { get; }
}

public sealed record SnmpV2cSourceConfiguration(
    SnmpRuntimeProfile Profile,
    SourceId SourceId,
    string Host,
    int Port,
    ProtocolSecretReference CommunityReference,
    IReadOnlyList<SnmpPointConfiguration> Points,
    SnmpRetryPolicy RetryPolicy)
{
    public Result Validate(SnmpConfigurationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (Profile != SnmpRuntimeProfile.NonProductionV2cReadOnly)
        {
            return Failure("snmp.profile", "Only the non-production SNMP v2c read-only profile is available.");
        }

        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253)
        {
            return Failure("snmp.host", "SNMP host is required and must be bounded.");
        }

        if (Port is < 1 or > 65535)
        {
            return Failure("snmp.port", "SNMP port must be between 1 and 65535.");
        }

        if (CommunityReference.Value is null)
        {
            return Failure("snmp.community_reference", "SNMP community secret reference is required.");
        }

        if (Points is null || Points.Count == 0 || Points.Count > limits.MaxPoints)
        {
            return Failure("snmp.point_capacity", "SNMP point count is outside the configured bound.");
        }

        if (Points.Select(point => point.PointId).Distinct().Count() != Points.Count ||
            Points.Select(point => point.Oid).Distinct().Count() != Points.Count)
        {
            return Failure("snmp.point_duplicate", "SNMP point identities and OIDs must be unique.");
        }

        foreach (var point in Points)
        {
            ArgumentNullException.ThrowIfNull(point.Unit);
            if (point.ValueType is < SnmpNumericType.Signed32 or > SnmpNumericType.Counter64)
            {
                return Failure("snmp.value_type", "SNMP value type is unsupported.");
            }

            var oid = SnmpOid.Parse(point.Oid.Value, limits.MaxOidArcs, limits.MaxOidBytes);
            if (oid.IsFailure || oid.Value != point.Oid)
            {
                return Failure("snmp.oid", "SNMP OID must be valid and canonical.");
            }
        }

        return Result.Success();
    }

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));
}
