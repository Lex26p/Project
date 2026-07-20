using Dispatcher.Core;
using Dispatcher.Semantics;

namespace Dispatcher.Modbus;

public enum ModbusRuntimeProfile
{
    NonProductionReadOnly = 1,
}

public enum ModbusRegisterTable
{
    HoldingRegisters = 3,
    InputRegisters = 4,
}

public enum ModbusValueType
{
    Signed16 = 1,
    Unsigned16 = 2,
    Signed32 = 3,
    Unsigned32 = 4,
}

public enum ModbusByteOrder
{
    BigEndian = 1,
    LittleEndian = 2,
}

public enum ModbusWordOrder
{
    HighWordFirst = 1,
    LowWordFirst = 2,
}

public sealed record ModbusPointConfiguration(
    PointId PointId,
    ModbusRegisterTable Table,
    int Address,
    ModbusValueType ValueType,
    ModbusByteOrder ByteOrder,
    ModbusWordOrder WordOrder,
    Unit Unit);

public sealed record ModbusRetryPolicy
{
    public ModbusRetryPolicy(int maxAttempts, TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAttempts, 10);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        MaxAttempts = maxAttempts;
        Delay = delay;
    }

    public int MaxAttempts { get; }
    public TimeSpan Delay { get; }
}

public sealed record ModbusConfigurationLimits
{
    public ModbusConfigurationLimits(int maxPoints, int maxRegistersPerPoll)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPoints);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRegistersPerPoll);
        MaxPoints = maxPoints;
        MaxRegistersPerPoll = maxRegistersPerPoll;
    }

    public int MaxPoints { get; }
    public int MaxRegistersPerPoll { get; }
}

public sealed record ModbusTcpSourceConfiguration(
    ModbusRuntimeProfile Profile,
    SourceId SourceId,
    string Host,
    int Port,
    int UnitId,
    IReadOnlyList<ModbusPointConfiguration> Points,
    ModbusRetryPolicy RetryPolicy)
{
    public Result Validate(ModbusConfigurationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (Profile != ModbusRuntimeProfile.NonProductionReadOnly)
        {
            return Failure("modbus.profile", "Only the non-production read-only Modbus profile is available.");
        }

        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253)
        {
            return Failure("modbus.host", "Modbus host is required and must be bounded.");
        }

        if (Port is < 1 or > 65535)
        {
            return Failure("modbus.port", "Modbus TCP port must be between 1 and 65535.");
        }

        if (UnitId is < 0 or > 255)
        {
            return Failure("modbus.unit_id", "Modbus Unit ID must be between 0 and 255.");
        }

        if (Points is null || Points.Count == 0 || Points.Count > limits.MaxPoints || Points.Count > ushort.MaxValue)
        {
            return Failure("modbus.point_capacity", "Modbus point count is outside the configured bound.");
        }

        if (Points.Select(point => point.PointId).Distinct().Count() != Points.Count)
        {
            return Failure("modbus.point_duplicate", "Modbus point identities must be unique.");
        }

        var totalRegisters = 0;
        foreach (var point in Points)
        {
            ArgumentNullException.ThrowIfNull(point.Unit);
            if (point.Table is not ModbusRegisterTable.HoldingRegisters and not ModbusRegisterTable.InputRegisters)
            {
                return Failure("modbus.table", "Only read holding/input register tables are supported.");
            }

            if (point.ValueType is < ModbusValueType.Signed16 or > ModbusValueType.Unsigned32)
            {
                return Failure("modbus.value_type", "Modbus value type is unsupported.");
            }

            if (point.ByteOrder is not ModbusByteOrder.BigEndian and not ModbusByteOrder.LittleEndian ||
                point.WordOrder is not ModbusWordOrder.HighWordFirst and not ModbusWordOrder.LowWordFirst)
            {
                return Failure("modbus.endian", "Modbus byte/word order is unsupported.");
            }

            var registerCount = RegisterCount(point.ValueType);
            if (point.Address < 0 || point.Address > 65535 || point.Address + registerCount > 65536)
            {
                return Failure("modbus.address", "Modbus zero-based register address is outside the valid range.");
            }

            if (registerCount == 1 && point.WordOrder != ModbusWordOrder.HighWordFirst)
            {
                return Failure("modbus.endian", "Word order is only applicable to multi-register values.");
            }

            totalRegisters = checked(totalRegisters + registerCount);
        }

        return totalRegisters <= limits.MaxRegistersPerPoll
            ? Result.Success()
            : Failure("modbus.register_capacity", "Modbus poll exceeds the configured register bound.");
    }

    internal static int RegisterCount(ModbusValueType valueType) => valueType switch
    {
        ModbusValueType.Signed16 or ModbusValueType.Unsigned16 => 1,
        ModbusValueType.Signed32 or ModbusValueType.Unsigned32 => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(valueType)),
    };

    private static Result Failure(string code, string message) =>
        Result.Failure(new OperationError(ErrorCode.From(code), message));
}
