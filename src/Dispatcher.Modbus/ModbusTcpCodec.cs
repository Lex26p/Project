using System.Buffers.Binary;
using Dispatcher.Core;
using Dispatcher.Protocols;
using Dispatcher.Semantics;

namespace Dispatcher.Modbus;

public static class ModbusTcpFrameCodec
{
    private static readonly IReadOnlyList<byte> ReadFunctionCodes = Array.AsReadOnly(new byte[]
    {
        (byte)ModbusRegisterTable.HoldingRegisters,
        (byte)ModbusRegisterTable.InputRegisters,
    });

    public static IReadOnlyList<byte> SupportedFunctionCodes => ReadFunctionCodes;

    public static byte[] BuildReadRequest(
        ushort transactionId,
        int unitId,
        ModbusPointConfiguration point)
    {
        ArgumentNullException.ThrowIfNull(point);
        var frame = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 6);
        frame[6] = checked((byte)unitId);
        frame[7] = checked((byte)point.Table);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), checked((ushort)point.Address));
        BinaryPrimitives.WriteUInt16BigEndian(
            frame.AsSpan(10),
            checked((ushort)ModbusTcpSourceConfiguration.RegisterCount(point.ValueType)));
        return frame;
    }
}

internal static class ModbusBatchCodec
{
    public static byte[] Encode(IReadOnlyList<ReadOnlyMemory<byte>> frames)
    {
        var length = checked(2 + frames.Sum(frame => checked(2 + frame.Length)));
        var payload = new byte[length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, checked((ushort)frames.Count));
        var offset = 2;
        foreach (var frame in frames)
        {
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(offset), checked((ushort)frame.Length));
            offset += 2;
            frame.Span.CopyTo(payload.AsSpan(offset));
            offset += frame.Length;
        }

        return payload;
    }

    public static Result<IReadOnlyList<ReadOnlyMemory<byte>>> Decode(
        ReadOnlyMemory<byte> payload,
        int expectedCount)
    {
        if (payload.Length < 2)
        {
            return Failure("modbus.batch", "Modbus response batch is truncated.");
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(payload.Span);
        if (count != expectedCount)
        {
            return Failure("modbus.batch_count", "Modbus response batch count does not match configuration.");
        }

        var frames = new ReadOnlyMemory<byte>[count];
        var offset = 2;
        for (var index = 0; index < count; index++)
        {
            if (offset + 2 > payload.Length)
            {
                return Failure("modbus.batch", "Modbus response batch is truncated.");
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(payload.Span[offset..]);
            offset += 2;
            if (length == 0 || offset + length > payload.Length)
            {
                return Failure("modbus.batch", "Modbus response frame length is invalid.");
            }

            frames[index] = payload.Slice(offset, length);
            offset += length;
        }

        return offset == payload.Length
            ? Result.Success<IReadOnlyList<ReadOnlyMemory<byte>>>(frames)
            : Failure("modbus.batch", "Modbus response batch contains trailing data.");
    }

    private static Result<IReadOnlyList<ReadOnlyMemory<byte>>> Failure(string code, string message) =>
        Result.Failure<IReadOnlyList<ReadOnlyMemory<byte>>>(
            new OperationError(ErrorCode.From(code), message));
}

public sealed class ModbusObservationParser : IProtocolObservationParser, IProtocolDiagnosticParser
{
    private readonly object sync = new();
    private readonly ModbusTcpSourceConfiguration configuration;
    private readonly IWallClock clock;
    private readonly Dictionary<PointId, long> lastValues = [];
    private ulong sourcePosition;

    public ModbusObservationParser(ModbusTcpSourceConfiguration configuration, IWallClock clock)
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
            return Failure<IReadOnlyList<SourceObservation>>(
                "modbus.binding",
                "Modbus configuration belongs to another source binding.");
        }

        var decoded = Decode(response);
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
        var decoded = Decode(response);
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

    private Result<IReadOnlyList<DecodedItem>> Decode(ReadOnlyMemory<byte> response)
    {
        var batch = ModbusBatchCodec.Decode(response, configuration.Points.Count);
        if (batch.IsFailure)
        {
            return Result.Failure<IReadOnlyList<DecodedItem>>(batch.Error!);
        }

        return Result.Success<IReadOnlyList<DecodedItem>>(batch.Value
            .Select((frame, index) => DecodeFrame(frame.Span, index, configuration.Points[index]))
            .ToArray());
    }

    private DecodedItem DecodeFrame(
        ReadOnlySpan<byte> frame,
        int index,
        ModbusPointConfiguration point)
    {
        if (frame.Length < 9 ||
            BinaryPrimitives.ReadUInt16BigEndian(frame) != checked((ushort)(index + 1)) ||
            BinaryPrimitives.ReadUInt16BigEndian(frame[2..]) != 0 ||
            BinaryPrimitives.ReadUInt16BigEndian(frame[4..]) != frame.Length - 6 ||
            frame[6] != configuration.UnitId)
        {
            return DecodedItem.Failed("modbus.response_header");
        }

        var expectedFunction = checked((byte)point.Table);
        if (frame[7] == (expectedFunction | 0x80))
        {
            return frame.Length == 9
                ? DecodedItem.Failed($"modbus.exception_{frame[8]}")
                : DecodedItem.Failed("modbus.exception_frame");
        }

        var expectedBytes = checked(ModbusTcpSourceConfiguration.RegisterCount(point.ValueType) * 2);
        if (frame[7] != expectedFunction || frame[8] != expectedBytes || frame.Length != 9 + expectedBytes)
        {
            return DecodedItem.Failed("modbus.response_shape");
        }

        Span<byte> normalized = stackalloc byte[4];
        frame.Slice(9, expectedBytes).CopyTo(normalized);
        if (point.ByteOrder == ModbusByteOrder.LittleEndian)
        {
            for (var offset = 0; offset < expectedBytes; offset += 2)
            {
                (normalized[offset], normalized[offset + 1]) = (normalized[offset + 1], normalized[offset]);
            }
        }

        if (expectedBytes == 4 && point.WordOrder == ModbusWordOrder.LowWordFirst)
        {
            (normalized[0], normalized[2]) = (normalized[2], normalized[0]);
            (normalized[1], normalized[3]) = (normalized[3], normalized[1]);
        }

        long value = point.ValueType switch
        {
            ModbusValueType.Signed16 => BinaryPrimitives.ReadInt16BigEndian(normalized),
            ModbusValueType.Unsigned16 => BinaryPrimitives.ReadUInt16BigEndian(normalized),
            ModbusValueType.Signed32 => BinaryPrimitives.ReadInt32BigEndian(normalized),
            ModbusValueType.Unsigned32 => BinaryPrimitives.ReadUInt32BigEndian(normalized),
            _ => throw new ArgumentOutOfRangeException(nameof(point)),
        };
        return new DecodedItem(true, value, "modbus.ok");
    }

    private static Result<TValue> Failure<TValue>(string code, string message) =>
        Result.Failure<TValue>(new OperationError(ErrorCode.From(code), message));

    private sealed record DecodedItem(bool Success, long Value, string Code)
    {
        public static DecodedItem Failed(string code) => new(false, 0, code);
    }
}
