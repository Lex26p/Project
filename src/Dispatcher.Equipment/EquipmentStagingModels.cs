using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dispatcher.Facilities;
using Dispatcher.Semantics;

namespace Dispatcher.Equipment;

public enum EquipmentProtocol
{
    ModbusTcp = 1,
    Snmp = 2,
}

public enum StagingRowState
{
    Reserved = 1,
    EquipmentAccepted = 2,
    Created = 3,
}

public sealed class WriteOnlySecret
{
    private readonly string value;

    private WriteOnlySecret(string value)
    {
        this.value = value;
    }

    public static WriteOnlySecret From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new WriteOnlySecret(value);
    }

    internal string Reveal() => value;

    public override string ToString() => "[REDACTED]";
}

public sealed record EquipmentProtocolForm(
    EquipmentProtocol Protocol,
    string Host,
    int Port,
    int? ModbusUnitId,
    string? SnmpVersion,
    WriteOnlySecret? Secret)
{
    public static EquipmentProtocolForm NewModbusTcp() =>
        new(EquipmentProtocol.ModbusTcp, string.Empty, 502, 1, null, null);

    public static EquipmentProtocolForm NewSnmp() =>
        new(EquipmentProtocol.Snmp, string.Empty, 161, null, "v2c", WriteOnlySecret.From("public"));
}

public sealed record StagingRowInput(
    Guid RowId,
    EquipmentId EquipmentId,
    FacilityScopeId ScopeId,
    LocationId LocationId,
    string Code,
    string Name,
    EquipmentProtocolForm Form);

public sealed record StagingFieldError(string Field, string Code, string Message);

public sealed record StagingRowResult(
    Guid RowId,
    EquipmentId EquipmentId,
    StagingRowState? State,
    IReadOnlyList<StagingFieldError> Errors)
{
    public bool Created => State == StagingRowState.Created && Errors.Count == 0;
}

public sealed record StagingRowSnapshot(
    Guid RowId,
    EquipmentId EquipmentId,
    FacilityScopeId ScopeId,
    LocationId LocationId,
    string Code,
    string Name,
    EquipmentProtocol Protocol,
    string Host,
    int Port,
    int? ModbusUnitId,
    string? SnmpVersion,
    bool HasSecret,
    StagingRowState State,
    long Version);

public sealed record StagingWorkItem(
    StagingRowSnapshot Snapshot,
    string RequestFingerprint,
    string FormDataJson,
    byte[]? ProtectedSecret);

public sealed record EquipmentStagingTemplate(
    string Name,
    EquipmentProtocol Protocol,
    string Host,
    int Port,
    int? ModbusUnitId,
    string? SnmpVersion)
{
    public EquipmentProtocolForm CreateForm() =>
        new(Protocol, Host, Port, ModbusUnitId, SnmpVersion, null);
}

public sealed class StagingSecretProtector
{
    private readonly byte[] key;

    public StagingSecretProtector(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("The staging secret key must contain 32 bytes.", nameof(key));
        }

        this.key = key.ToArray();
    }

    public byte[] Protect(WriteOnlySecret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var plaintext = Encoding.UTF8.GetBytes(secret.Reveal());
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return [.. nonce, .. tag, .. ciphertext];
    }
}

internal static class StagingFingerprint
{
    public static string Compute(StagingRowInput input)
    {
        var secretHash = input.Form.Secret is null
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Form.Secret.Reveal())));
        var payload = JsonSerializer.Serialize(new
        {
            input.EquipmentId.Value,
            ScopeId = input.ScopeId.Value,
            LocationId = input.LocationId.Value,
            input.Code,
            input.Name,
            input.Form.Protocol,
            input.Form.Host,
            input.Form.Port,
            input.Form.ModbusUnitId,
            input.Form.SnmpVersion,
            SecretHash = secretHash,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
