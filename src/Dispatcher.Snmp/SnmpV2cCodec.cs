using System.Buffers.Binary;
using System.Security.Cryptography;
using Dispatcher.Core;
using Dispatcher.Protocols;
using Dispatcher.Semantics;

namespace Dispatcher.Snmp;

public sealed record SnmpWireLimits
{
    public SnmpWireLimits(int maxCommunityBytes, int maxRequestBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCommunityBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRequestBytes);
        MaxCommunityBytes = maxCommunityBytes;
        MaxRequestBytes = maxRequestBytes;
    }

    public int MaxCommunityBytes { get; }
    public int MaxRequestBytes { get; }
}

public static class SnmpV2cCodec
{
    private const byte Sequence = 0x30;
    private const byte Integer = 0x02;
    private const byte OctetString = 0x04;
    private const byte Null = 0x05;
    private const byte ObjectIdentifier = 0x06;
    private const byte GetRequest = 0xA0;
    private const byte GetResponse = 0xA2;
    private static readonly IReadOnlyList<byte> RequestPduTypes = Array.AsReadOnly(new byte[] { GetRequest });

    public static IReadOnlyList<byte> SupportedRequestPduTypes => RequestPduTypes;

    public static Result<byte[]> BuildGetRequest(
        int requestId,
        ReadOnlySpan<byte> community,
        IReadOnlyList<SnmpOid> oids,
        SnmpWireLimits limits)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestId);
        ArgumentNullException.ThrowIfNull(oids);
        ArgumentNullException.ThrowIfNull(limits);
        if (community.Length == 0 || community.Length > limits.MaxCommunityBytes)
        {
            return Failure<byte[]>("snmp.community_size", "SNMP community exceeds the configured secret bound.");
        }

        if (oids.Count == 0)
        {
            return Failure<byte[]>("snmp.oid_count", "SNMP GET requires at least one OID.");
        }

        var variableBindings = oids.Select(oid => Tlv(Sequence, Concat(
            Tlv(ObjectIdentifier, EncodeOid(oid)),
            Tlv(Null, [])))).ToArray();
        var pdu = Tlv(GetRequest, Concat(
            Tlv(Integer, EncodePositiveInteger(requestId)),
            Tlv(Integer, [0]),
            Tlv(Integer, [0]),
            Tlv(Sequence, Concat(variableBindings))));
        var message = Tlv(Sequence, Concat(
            Tlv(Integer, [1]),
            Tlv(OctetString, community.ToArray()),
            pdu));
        return message.Length <= limits.MaxRequestBytes
            ? Result.Success(message)
            : Failure<byte[]>("snmp.request_size", "SNMP GET exceeds the configured request byte bound.");
    }

    internal static byte[] WrapResponse(int requestId, ReadOnlyMemory<byte> response)
    {
        var wrapped = new byte[checked(4 + response.Length)];
        BinaryPrimitives.WriteInt32BigEndian(wrapped, requestId);
        response.Span.CopyTo(wrapped.AsSpan(4));
        return wrapped;
    }

    internal static bool ResponseCommunityMatches(
        ReadOnlyMemory<byte> response,
        ReadOnlySpan<byte> expectedCommunity)
    {
        var root = new BerReader(response.Span);
        if (!root.TryRead(Sequence, out var message) || !root.End)
        {
            return false;
        }

        var messageReader = new BerReader(message);
        return messageReader.TryRead(Integer, out _) &&
               messageReader.TryRead(OctetString, out var community) &&
               community.Length == expectedCommunity.Length &&
               CryptographicOperations.FixedTimeEquals(community, expectedCommunity);
    }

    internal static Result<IReadOnlyList<SnmpDecodedItem>> DecodeResponse(
        ReadOnlyMemory<byte> wrappedResponse,
        IReadOnlyList<SnmpPointConfiguration> points)
    {
        if (wrappedResponse.Length < 5)
        {
            return Malformed("SNMP response envelope is truncated.");
        }

        var expectedRequestId = BinaryPrimitives.ReadInt32BigEndian(wrappedResponse.Span);
        if (expectedRequestId <= 0)
        {
            return Malformed("SNMP response envelope request ID is invalid.");
        }

        var root = new BerReader(wrappedResponse.Span[4..]);
        if (!root.TryRead(Sequence, out var message) || !root.End)
        {
            return Malformed("SNMP message sequence is malformed.");
        }

        var messageReader = new BerReader(message);
        if (!messageReader.TryRead(Integer, out var version) ||
            !TryReadSigned(version, 4, out var versionValue) || versionValue != 1 ||
            !messageReader.TryRead(OctetString, out var community) || community.Length == 0 ||
            !messageReader.TryRead(GetResponse, out var responsePdu) || !messageReader.End)
        {
            return Malformed("SNMP v2c response envelope is invalid.");
        }

        var pduReader = new BerReader(responsePdu);
        if (!pduReader.TryRead(Integer, out var requestIdBytes) ||
            !TryReadSigned(requestIdBytes, 4, out var requestId) || requestId != expectedRequestId ||
            !pduReader.TryRead(Integer, out var errorStatusBytes) ||
            !TryReadSigned(errorStatusBytes, 4, out var errorStatus) || errorStatus is < 0 or > int.MaxValue ||
            !pduReader.TryRead(Integer, out var errorIndexBytes) ||
            !TryReadSigned(errorIndexBytes, 4, out var errorIndex) || errorIndex is < 0 or > int.MaxValue ||
            !pduReader.TryRead(Sequence, out var variableBindings) || !pduReader.End)
        {
            return Malformed("SNMP response PDU is malformed.");
        }

        var bindingsReader = new BerReader(variableBindings);
        var items = new List<SnmpDecodedItem>(points.Count);
        for (var index = 0; index < points.Count; index++)
        {
            if (!bindingsReader.TryRead(Sequence, out var variableBinding))
            {
                return Malformed("SNMP response variable binding count is incomplete.");
            }

            var bindingReader = new BerReader(variableBinding);
            if (!bindingReader.TryRead(ObjectIdentifier, out var oidBytes) ||
                !TryDecodeOid(oidBytes, out var oid) || oid != points[index].Oid ||
                !bindingReader.TryReadAny(out var valueTag, out var valueBytes) || !bindingReader.End)
            {
                return Malformed("SNMP response variable binding is malformed or reordered.");
            }

            items.Add(errorStatus == 0
                ? DecodeValue(points[index].ValueType, valueTag, valueBytes)
                : SnmpDecodedItem.Failed($"snmp.error_status_{errorStatus}"));
        }

        return bindingsReader.End
            ? Result.Success<IReadOnlyList<SnmpDecodedItem>>(items)
            : Malformed("SNMP response contains unexpected variable bindings.");
    }

    private static SnmpDecodedItem DecodeValue(
        SnmpNumericType expectedType,
        byte tag,
        ReadOnlySpan<byte> value)
    {
        if (tag is 0x80 or 0x81 or 0x82)
        {
            return SnmpDecodedItem.Failed(tag switch
            {
                0x80 => "snmp.no_such_object",
                0x81 => "snmp.no_such_instance",
                _ => "snmp.end_of_mib_view",
            });
        }

        var expectedTag = expectedType switch
        {
            SnmpNumericType.Signed32 => Integer,
            SnmpNumericType.Counter32 => 0x41,
            SnmpNumericType.Gauge32 => 0x42,
            SnmpNumericType.TimeTicks => 0x43,
            SnmpNumericType.Counter64 => 0x46,
            _ => 0,
        };
        if (tag != expectedTag)
        {
            return SnmpDecodedItem.Failed("snmp.value_type");
        }

        if (expectedType == SnmpNumericType.Signed32)
        {
            return TryReadSigned(value, 4, out var signed) && signed is >= int.MinValue and <= int.MaxValue
                ? new SnmpDecodedItem(true, signed, "snmp.ok")
                : SnmpDecodedItem.Failed("snmp.value_range");
        }

        return TryReadUnsigned(value, expectedType == SnmpNumericType.Counter64 ? 9 : 5, out var unsigned) &&
               unsigned <= long.MaxValue
            ? new SnmpDecodedItem(true, checked((long)unsigned), "snmp.ok")
            : SnmpDecodedItem.Failed("snmp.value_range");
    }

    private static bool TryReadSigned(ReadOnlySpan<byte> bytes, int maxBytes, out long value)
    {
        value = 0;
        if (bytes.Length == 0 || bytes.Length > maxBytes)
        {
            return false;
        }

        value = (sbyte)bytes[0];
        for (var index = 1; index < bytes.Length; index++)
        {
            value = (value << 8) | bytes[index];
        }

        return true;
    }

    private static bool TryReadUnsigned(ReadOnlySpan<byte> bytes, int maxBytes, out ulong value)
    {
        value = 0;
        if (bytes.Length == 0 || bytes.Length > maxBytes || bytes[0] >= 0x80 ||
            bytes.Length == 9 && bytes[0] != 0)
        {
            return false;
        }

        foreach (var item in bytes)
        {
            value = (value << 8) | item;
        }

        return true;
    }

    private static byte[] EncodePositiveInteger(int value)
    {
        Span<byte> buffer = stackalloc byte[5];
        var offset = buffer.Length;
        var remaining = checked((uint)value);
        do
        {
            buffer[--offset] = checked((byte)remaining);
            remaining >>= 8;
        }
        while (remaining != 0);
        if (buffer[offset] >= 0x80)
        {
            buffer[--offset] = 0;
        }

        return buffer[offset..].ToArray();
    }

    private static byte[] EncodeOid(SnmpOid oid)
    {
        var arcs = oid.GetArcs();
        var encoded = new List<byte>();
        AppendBase128(encoded, checked((ulong)(arcs[0] * 40) + arcs[1]));
        for (var index = 2; index < arcs.Length; index++)
        {
            AppendBase128(encoded, arcs[index]);
        }

        return encoded.ToArray();
    }

    private static bool TryDecodeOid(ReadOnlySpan<byte> bytes, out SnmpOid oid)
    {
        oid = default;
        var values = new List<ulong>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            ulong value = 0;
            var octets = 0;
            byte current;
            do
            {
                if (offset >= bytes.Length || ++octets > 5)
                {
                    return false;
                }

                current = bytes[offset++];
                value = (value << 7) | (uint)(current & 0x7F);
            }
            while ((current & 0x80) != 0);
            values.Add(value);
        }

        if (values.Count == 0)
        {
            return false;
        }

        var first = values[0] < 40 ? 0UL : values[0] < 80 ? 1UL : 2UL;
        var second = values[0] - first * 40;
        var text = string.Join('.', new[] { first, second }.Concat(values.Skip(1)));
        var parsed = SnmpOid.Parse(text, maxArcs: 128, maxEncodedBytes: 512);
        if (parsed.IsFailure)
        {
            return false;
        }

        oid = parsed.Value;
        return true;
    }

    private static void AppendBase128(List<byte> output, ulong value)
    {
        Span<byte> buffer = stackalloc byte[10];
        var offset = buffer.Length;
        buffer[--offset] = checked((byte)(value & 0x7F));
        while ((value >>= 7) != 0)
        {
            buffer[--offset] = checked((byte)((value & 0x7F) | 0x80));
        }

        output.AddRange(buffer[offset..].ToArray());
    }

    private static byte[] Tlv(byte tag, ReadOnlySpan<byte> value) =>
        Concat([tag], EncodeLength(value.Length), value.ToArray());

    private static byte[] EncodeLength(int length)
    {
        if (length < 0x80)
        {
            return [checked((byte)length)];
        }

        Span<byte> buffer = stackalloc byte[4];
        var offset = buffer.Length;
        var remaining = checked((uint)length);
        while (remaining != 0)
        {
            buffer[--offset] = checked((byte)remaining);
            remaining >>= 8;
        }

        return [checked((byte)(0x80 | buffer.Length - offset)), .. buffer[offset..].ToArray()];
    }

    private static byte[] Concat(params IReadOnlyList<byte>[] parts)
    {
        var result = new byte[parts.Sum(part => part.Count)];
        var offset = 0;
        foreach (var part in parts)
        {
            for (var index = 0; index < part.Count; index++)
            {
                result[offset++] = part[index];
            }
        }

        return result;
    }

    private static Result<IReadOnlyList<SnmpDecodedItem>> Malformed(string message) =>
        Failure<IReadOnlyList<SnmpDecodedItem>>("snmp.response_malformed", message);

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));

    private ref struct BerReader
    {
        private readonly ReadOnlySpan<byte> bytes;
        private int offset;

        public BerReader(ReadOnlySpan<byte> bytes)
        {
            this.bytes = bytes;
            offset = 0;
        }

        public bool End => offset == bytes.Length;

        public bool TryRead(byte expectedTag, out ReadOnlySpan<byte> value)
        {
            if (!TryReadAny(out var tag, out value) || tag != expectedTag)
            {
                value = default;
                return false;
            }

            return true;
        }

        public bool TryReadAny(out byte tag, out ReadOnlySpan<byte> value)
        {
            tag = 0;
            value = default;
            if (offset + 2 > bytes.Length)
            {
                return false;
            }

            tag = bytes[offset++];
            var firstLength = bytes[offset++];
            int length;
            if ((firstLength & 0x80) == 0)
            {
                length = firstLength;
            }
            else
            {
                var count = firstLength & 0x7F;
                if (count is 0 or > 4 || offset + count > bytes.Length)
                {
                    return false;
                }

                length = 0;
                for (var index = 0; index < count; index++)
                {
                    length = checked((length << 8) | bytes[offset++]);
                }
            }

            if (length < 0 || offset + length > bytes.Length)
            {
                return false;
            }

            value = bytes.Slice(offset, length);
            offset += length;
            return true;
        }
    }
}

internal sealed record SnmpDecodedItem(bool Success, long Value, string Code)
{
    public static SnmpDecodedItem Failed(string code) => new(false, 0, code);
}

public sealed class SnmpObservationParser : IProtocolObservationParser, IProtocolDiagnosticParser
{
    private readonly object sync = new();
    private readonly SnmpV2cSourceConfiguration configuration;
    private readonly IWallClock clock;
    private readonly Dictionary<PointId, long> lastValues = [];
    private ulong sourcePosition;

    public SnmpObservationParser(SnmpV2cSourceConfiguration configuration, IWallClock clock)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(clock);
        this.configuration = configuration;
        this.clock = clock;
    }

    public Result<IReadOnlyList<SourceObservation>> Parse(
        SourceBinding binding,
        ReadOnlyMemory<byte> response)
    {
        if (binding.SourceId != configuration.SourceId)
        {
            return Failure<IReadOnlyList<SourceObservation>>("snmp.binding", "SNMP source binding is invalid.");
        }

        var decoded = SnmpV2cCodec.DecodeResponse(response, configuration.Points);
        if (decoded.IsFailure)
        {
            return Result.Failure<IReadOnlyList<SourceObservation>>(decoded.Error!);
        }

        lock (sync)
        {
            var timestamp = SourceTimestamp.FromUtc(clock.GetUtcNow());
            var observations = new SourceObservation[decoded.Value.Count];
            for (var index = 0; index < decoded.Value.Count; index++)
            {
                var point = configuration.Points[index];
                var item = decoded.Value[index];
                var value = item.Success ? item.Value : lastValues.GetValueOrDefault(point.PointId);
                if (item.Success)
                {
                    lastValues[point.PointId] = value;
                }

                sourcePosition = checked(sourcePosition + 1);
                observations[index] = new SourceObservation(
                    binding.ScopeId,
                    binding.SourceId,
                    point.PointId,
                    new OwnerPosition<SourceObservation>(sourcePosition),
                    TypedValue.From(value),
                    point.Unit,
                    item.Success ? DataQuality.Good : DataQuality.Bad,
                    item.Success ? Freshness.Fresh : Freshness.Stale,
                    timestamp);
            }

            return Result.Success<IReadOnlyList<SourceObservation>>(observations);
        }
    }

    public Result<ProtocolDiagnosticBatch> ParseDiagnostic(ReadOnlyMemory<byte> response)
    {
        var decoded = SnmpV2cCodec.DecodeResponse(response, configuration.Points);
        if (decoded.IsFailure)
        {
            return Result.Failure<ProtocolDiagnosticBatch>(decoded.Error!);
        }

        var samples = decoded.Value.Select((item, index) => new ProtocolDiagnosticSample(
            configuration.Points[index].PointId,
            item.Value,
            configuration.Points[index].Unit,
            item.Success ? DataQuality.Good : DataQuality.Bad,
            item.Code)).ToArray();
        return Result.Success(new ProtocolDiagnosticBatch(samples, decoded.Value.Any(item => !item.Success)));
    }

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));
}
