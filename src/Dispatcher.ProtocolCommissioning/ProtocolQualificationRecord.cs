using System.Security.Cryptography;
using System.Text;
using Dispatcher.Semantics;

namespace Dispatcher.ProtocolCommissioning;

public enum QualifiedProtocolProfile
{
    ModbusTcpReadOnly = 1,
    SnmpV2cReadOnly = 2,
}

public enum ProtocolQualificationStatus
{
    WindowsQualifiedPlatformPending = 1,
    Qualified = 2,
}

public sealed record ProtocolQualificationEvidence(
    bool EndToEndConsumers,
    bool MultiDeviceLoad,
    bool ReconnectStorm,
    bool ProcessCrashRecovery,
    bool ReadOnlySurfaceFrozen,
    bool WindowsX64,
    bool LinuxX64);

public sealed record ProtocolQualificationRecord(
    QualifiedProtocolProfile Profile,
    string ContractFingerprint,
    ProtocolQualificationStatus Status,
    ProtocolQualificationEvidence Evidence)
{
    public bool ClosesDg07 => Status == ProtocolQualificationStatus.Qualified;

    public static Result<ProtocolQualificationRecord> Create(
        QualifiedProtocolProfile profile,
        ProtocolQualificationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!evidence.EndToEndConsumers || !evidence.MultiDeviceLoad || !evidence.ReconnectStorm ||
            !evidence.ProcessCrashRecovery || !evidence.ReadOnlySurfaceFrozen || !evidence.WindowsX64)
        {
            return Result.Failure<ProtocolQualificationRecord>(new OperationError(
                ErrorCode.From("protocol.qualification_incomplete"),
                "Required protocol qualification evidence is incomplete."));
        }

        return Result.Success(new ProtocolQualificationRecord(
            profile,
            ProtocolContractFreeze.Fingerprint(profile),
            evidence.LinuxX64
                ? ProtocolQualificationStatus.Qualified
                : ProtocolQualificationStatus.WindowsQualifiedPlatformPending,
            evidence));
    }
}

public static class ProtocolContractFreeze
{
    private const string Modbus = "modbus-tcp|non-production-read-only|fc03,fc04|no-write";
    private const string Snmp = "snmp-v2c|non-production-read-only|get,get-response|no-set,no-trap";

    public static string Fingerprint(QualifiedProtocolProfile profile)
    {
        var contract = profile switch
        {
            QualifiedProtocolProfile.ModbusTcpReadOnly => Modbus,
            QualifiedProtocolProfile.SnmpV2cReadOnly => Snmp,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contract)));
    }
}
