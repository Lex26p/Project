using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Dispatcher.IntegrationTests;

internal sealed record FakeSnmpRequest(
    int RequestId,
    byte PduType,
    byte[] Community,
    IReadOnlyList<byte[]> Oids);

internal sealed class FakeSnmpUdpPeer : IAsyncDisposable
{
    private readonly UdpClient client = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource cancellation = new();
    private readonly Func<FakeSnmpRequest, int, CancellationToken, ValueTask<byte[]?>> respond;
    private readonly ConcurrentQueue<byte> pduTypes = [];
    private readonly Task loop;
    private int requestCount;

    public FakeSnmpUdpPeer(
        Func<FakeSnmpRequest, int, CancellationToken, ValueTask<byte[]?>> respond)
    {
        this.respond = respond ?? throw new ArgumentNullException(nameof(respond));
        Port = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        loop = RunAsync(cancellation.Token);
    }

    public int Port { get; }

    public int RequestCount => Volatile.Read(ref requestCount);

    public IReadOnlyList<byte> PduTypes => pduTypes.ToArray();

    public static byte[] Response(
        FakeSnmpRequest request,
        IReadOnlyList<(byte Tag, byte[] Value)> values,
        int? requestId = null,
        byte[]? community = null,
        int errorStatus = 0)
    {
        if (values.Count != request.Oids.Count)
        {
            throw new ArgumentException(
                "Fake SNMP response values must match requested OIDs.",
                nameof(values));
        }

        var variables = request.Oids
            .Select((oid, index) => Tlv(
                0x30,
                Concat(
                    Tlv(0x06, oid),
                    Tlv(values[index].Tag, values[index].Value))))
            .ToArray();
        return Tlv(
            0x30,
            Concat(
                Tlv(0x02, [1]),
                Tlv(0x04, community ?? request.Community),
                Tlv(
                    0xA2,
                    Concat(
                        Tlv(0x02, PositiveInteger(requestId ?? request.RequestId)),
                        Tlv(0x02, PositiveInteger(errorStatus)),
                        Tlv(0x02, [0]),
                        Tlv(0x30, Concat(variables))))));
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        client.Dispose();
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }

        cancellation.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var datagram = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            var request = Parse(datagram.Buffer);
            pduTypes.Enqueue(request.PduType);
            var response = await respond(
                request,
                Interlocked.Increment(ref requestCount),
                cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                await client.SendAsync(
                    response,
                    datagram.RemoteEndPoint,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static FakeSnmpRequest Parse(ReadOnlySpan<byte> packet)
    {
        var packetOffset = 0;
        var root = Read(packet, ref packetOffset, 0x30);
        if (packetOffset != packet.Length)
        {
            throw new InvalidDataException("Fake SNMP request contains trailing data.");
        }

        var messageOffset = 0;
        _ = Read(root, ref messageOffset, 0x02);
        var community = Read(root, ref messageOffset, 0x04);
        var pdu = ReadAny(root, ref messageOffset);
        if (messageOffset != root.Length)
        {
            throw new InvalidDataException("Fake SNMP request envelope is invalid.");
        }

        var pduOffset = 0;
        var requestId = ReadInteger(Read(pdu.Value, ref pduOffset, 0x02));
        _ = Read(pdu.Value, ref pduOffset, 0x02);
        _ = Read(pdu.Value, ref pduOffset, 0x02);
        var bindings = Read(pdu.Value, ref pduOffset, 0x30);
        if (pduOffset != pdu.Value.Length)
        {
            throw new InvalidDataException("Fake SNMP request PDU is invalid.");
        }

        var oids = new List<byte[]>();
        var bindingOffset = 0;
        while (bindingOffset < bindings.Length)
        {
            var binding = Read(bindings, ref bindingOffset, 0x30);
            var variableOffset = 0;
            oids.Add(Read(binding, ref variableOffset, 0x06));
            _ = Read(binding, ref variableOffset, 0x05);
            if (variableOffset != binding.Length)
            {
                throw new InvalidDataException("Fake SNMP variable binding is invalid.");
            }
        }

        return new FakeSnmpRequest(
            requestId,
            pdu.Tag,
            community,
            oids);
    }

    private static TlvItem ReadAny(ReadOnlySpan<byte> bytes, ref int offset)
    {
        if (offset + 2 > bytes.Length)
        {
            throw new InvalidDataException("Fake SNMP TLV is truncated.");
        }

        var tag = bytes[offset++];
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
                throw new InvalidDataException("Fake SNMP TLV length is invalid.");
            }

            length = 0;
            for (var index = 0; index < count; index++)
            {
                length = checked((length << 8) | bytes[offset++]);
            }
        }

        if (offset + length > bytes.Length)
        {
            throw new InvalidDataException("Fake SNMP TLV value is truncated.");
        }

        var value = bytes.Slice(offset, length).ToArray();
        offset += length;
        return new TlvItem(tag, value);
    }

    private static byte[] Read(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        byte expectedTag)
    {
        var item = ReadAny(bytes, ref offset);
        return item.Tag == expectedTag
            ? item.Value
            : throw new InvalidDataException("Fake SNMP TLV tag is invalid.");
    }

    private static int ReadInteger(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > 4)
        {
            throw new InvalidDataException("Fake SNMP integer is invalid.");
        }

        var value = 0;
        foreach (var item in bytes)
        {
            value = checked((value << 8) | item);
        }

        return value;
    }

    private static byte[] PositiveInteger(int value)
    {
        if (value == 0)
        {
            return [0];
        }

        var bytes = new List<byte>();
        var remaining = value;
        while (remaining > 0)
        {
            bytes.Insert(0, checked((byte)remaining));
            remaining >>= 8;
        }

        if (bytes[0] >= 0x80)
        {
            bytes.Insert(0, 0);
        }

        return bytes.ToArray();
    }

    private static byte[] Tlv(byte tag, byte[] value)
    {
        var length = value.Length < 0x80
            ? new byte[] { checked((byte)value.Length) }
            : new byte[]
            {
                0x82,
                checked((byte)(value.Length >> 8)),
                checked((byte)value.Length),
            };
        return [tag, .. length, .. value];
    }

    private static byte[] Concat(params byte[][] parts) =>
        parts.SelectMany(part => part).ToArray();

    private sealed record TlvItem(byte Tag, byte[] Value);
}
