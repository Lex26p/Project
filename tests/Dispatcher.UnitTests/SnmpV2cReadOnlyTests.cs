using System.Text;
using System.Text.Json;
using Dispatcher.Core;
using Dispatcher.Equipment;
using Dispatcher.Protocols;
using Dispatcher.Semantics;
using Dispatcher.Snmp;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class SnmpV2cReadOnlyTests
{
    private const string OldCommunity = "private-old";
    private const string NewCommunity = "private-new";
    private static readonly RuntimeScopeId ScopeId = RuntimeScopeId.From(
        Guid.Parse("c1000000-0000-0000-0000-000000000001"));
    private static readonly SourceId SourceId = SourceId.From(
        Guid.Parse("c2000000-0000-0000-0000-000000000001"));
    private static readonly PointId PointA = PointId.From(
        Guid.Parse("c3000000-0000-0000-0000-000000000001"));
    private static readonly PointId PointB = PointId.From(
        Guid.Parse("c3000000-0000-0000-0000-000000000002"));
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProtocolWorkloadIdentity Workload =
        ProtocolWorkloadIdentity.From("dispatcher-runtime:windows-x64");
    private static readonly ProtocolSecretReference CommunityReference =
        ProtocolSecretReference.From("env:SNMP_COMMUNITY");
    private static readonly SnmpConfigurationLimits ConfigurationLimits = new(16, 32, 128);
    private static readonly SnmpWireLimits WireLimits = new(128, 4096);
    private static readonly ProtocolIoLimits IoLimits = new(TimeSpan.FromSeconds(1), 4096, 16, 1);
    private static readonly SnmpOid OidA = Oid("1.3.6.1.2.1.1.3.0");
    private static readonly SnmpOid OidB = Oid("1.3.6.1.2.1.2.2.1.10.1");

    [Fact]
    public void ProfileNormalizesOidAndExposesOnlyGetRequest()
    {
        var normalized = SnmpOid.Parse(".1.3.6.1.2.1.1.3.0", 32, 128);
        Assert.Equal("1.3.6.1.2.1.1.3.0", normalized.Value.Value);
        Assert.Equal("snmp.oid", SnmpOid.Parse("1.40.1", 32, 128).Error?.Code.Value);
        Assert.Equal(new byte[] { 0xA0 }, SnmpV2cCodec.SupportedRequestPduTypes);
        Assert.True(Configuration(Points()).Validate(ConfigurationLimits).IsSuccess);
        Assert.Equal(
            "snmp.profile",
            (Configuration(Points()) with { Profile = (SnmpRuntimeProfile)99 })
            .Validate(ConfigurationLimits).Error?.Code.Value);

        var newForm = EquipmentProtocolForm.NewSnmp();
        Assert.Equal("v2c", newForm.SnmpVersion);
        Assert.Equal("[REDACTED]", newForm.Secret!.ToString());
        Assert.DoesNotContain("public", JsonSerializer.Serialize(newForm), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcquisitionNormalizesValuesAndMapsPerOidErrorsToQuality()
    {
        var response = Response(
            1,
            OldCommunity,
            0,
            Variable(OidA, 0x02, [0xFB]),
            Variable(OidB, 0x81, []));
        using var source = CreateSource(
            Configuration(Points()),
            new MutableSecretResolver(OldCommunity),
            new QueueClientFactory(new StaticClient(response)));

        var acquired = await source.AcquireAsync(Binding(), 1);

        Assert.True(acquired.IsSuccess);
        Assert.Equal(-5, acquired.Value.Observations[0].Value.Value);
        Assert.Equal(DataQuality.Good, acquired.Value.Observations[0].Quality);
        Assert.Equal(DataQuality.Bad, acquired.Value.Observations[1].Quality);
        Assert.Equal(Freshness.Stale, acquired.Value.Observations[1].Freshness);
    }

    [Fact]
    public async Task PduErrorAndMalformedResponseAreIsolatedFromNextValidPoll()
    {
        var factory = new QueueClientFactory(
            new StaticClient(Response(
                1,
                OldCommunity,
                2,
                Variable(OidA, 0x02, [1]),
                Variable(OidB, 0x41, [2]))),
            new StaticClient(new byte[] { 0x30, 0x82, 0x7F }),
            new StaticClient(Response(
                3,
                OldCommunity,
                0,
                Variable(OidA, 0x02, [3]),
                Variable(OidB, 0x41, [4]))));
        using var source = CreateSource(Configuration(Points()), new MutableSecretResolver(OldCommunity), factory);

        var pduError = await source.AcquireAsync(Binding(), 1);
        var malformed = await source.AcquireAsync(Binding(), 2);
        var valid = await source.AcquireAsync(Binding(), 3);

        Assert.All(pduError.Value.Observations, observation => Assert.Equal(DataQuality.Bad, observation.Quality));
        Assert.Equal("protocol.io_failed", malformed.Error?.Code.Value);
        Assert.True(valid.IsSuccess);
        Assert.Equal((ulong)3, valid.Value.Observations[0].SourcePosition.Value);
        Assert.Equal(3, valid.Value.Observations[0].Value.Value);
    }

    [Fact]
    public async Task TimeoutUsesBoundedRetriesAndMapsToProtocolTimeout()
    {
        var factory = new QueueClientFactory(new TimeoutClient(), new TimeoutClient());
        using var source = CreateSource(
            Configuration(Points()) with
            {
                RetryPolicy = new SnmpRetryPolicy(2, TimeSpan.FromMilliseconds(20), TimeSpan.Zero),
            },
            new MutableSecretResolver(OldCommunity),
            factory);

        var timedOut = await source.AcquireAsync(Binding(), 1);

        Assert.Equal("protocol.io_timeout", timedOut.Error?.Code.Value);
        Assert.Equal(2, factory.OpenAttempts);
    }

    [Fact]
    public async Task DiagnosticsDoNotAdvanceSourcePositionOrMutateCurrent()
    {
        var factory = new QueueClientFactory(
            new StaticClient(Response(1, OldCommunity, 0, Variable(OidA, 0x02, [1]))),
            new StaticClient(Response(
                2,
                OldCommunity,
                0,
                Variable(OidA, 0x02, [2]),
                Variable(OidB, 0x41, [3]))),
            new StaticClient(Response(
                3,
                OldCommunity,
                0,
                Variable(OidA, 0x02, [4]),
                Variable(OidB, 0x41, [5]))));
        using var source = CreateSource(Configuration(Points()), new MutableSecretResolver(OldCommunity), factory);
        var binding = Binding();
        var clock = new FixedClock();
        var core = new CoreRuntime(ScopeId, clock, clock, new RuntimeCurrentLimits(4, 8));
        Assert.True(core.ActivateBinding(binding).IsSuccess);

        Assert.True((await source.ConnectionTestAsync(binding)).IsSuccess);
        var sample = await source.SamplePollAsync(binding);
        Assert.Empty(core.GetSnapshot().Entries);
        var acquired = await source.AcquireAsync(binding, 1);

        Assert.Equal(2, sample.Value.Samples.Count);
        Assert.Equal((ulong)1, acquired.Value.Observations[0].SourcePosition.Value);
        Assert.True(core.Apply(acquired.Value).IsSuccess);
    }

    [Fact]
    public async Task SecretReferenceMasksCommunityAndResolvesReplacementAfterRestart()
    {
        var resolver = new MutableSecretResolver(OldCommunity);
        var requests = new List<byte[]>();
        var firstFactory = new QueueClientFactory(new CapturingClient(
            requests,
            Response(
                1,
                OldCommunity,
                0,
                Variable(OidA, 0x02, [1]),
                Variable(OidB, 0x41, [2]))));
        var configuration = Configuration(Points());
        using (var first = CreateSource(configuration, resolver, firstFactory))
        {
            Assert.True((await first.AcquireAsync(Binding(), 1)).IsSuccess);
        }

        resolver.Replace(NewCommunity);
        var secondFactory = new QueueClientFactory(new CapturingClient(
            requests,
            Response(
                1,
                NewCommunity,
                0,
                Variable(OidA, 0x02, [3]),
                Variable(OidB, 0x41, [4]))));
        using (var restarted = CreateSource(configuration, resolver, secondFactory))
        {
            Assert.True((await restarted.AcquireAsync(Binding(), 1)).IsSuccess);
        }

        Assert.Contains(OldCommunity, Encoding.UTF8.GetString(requests[0]), StringComparison.Ordinal);
        Assert.Contains(NewCommunity, Encoding.UTF8.GetString(requests[1]), StringComparison.Ordinal);
        var persistedContract = JsonSerializer.Serialize(configuration);
        Assert.DoesNotContain(OldCommunity, persistedContract, StringComparison.Ordinal);
        Assert.DoesNotContain(NewCommunity, persistedContract, StringComparison.Ordinal);
        Assert.Contains(CommunityReference.Value, persistedContract, StringComparison.Ordinal);
    }

    private static SnmpV2cSource CreateSource(
        SnmpV2cSourceConfiguration configuration,
        IProtocolSecretResolver resolver,
        ISnmpDatagramClientFactory factory) =>
        SnmpV2cSource.Create(
            configuration,
            ConfigurationLimits,
            WireLimits,
            Workload,
            IoLimits,
            resolver,
            factory,
            new FixedClock()).Value;

    private static SnmpV2cSourceConfiguration Configuration(IReadOnlyList<SnmpPointConfiguration> points) => new(
        SnmpRuntimeProfile.NonProductionV2cReadOnly,
        SourceId,
        "127.0.0.1",
        161,
        CommunityReference,
        points,
        new SnmpRetryPolicy(1, TimeSpan.FromMilliseconds(100), TimeSpan.Zero));

    private static IReadOnlyList<SnmpPointConfiguration> Points() =>
    [
        new SnmpPointConfiguration(PointA, OidA, SnmpNumericType.Signed32, Unit.FromSymbol("s")),
        new SnmpPointConfiguration(PointB, OidB, SnmpNumericType.Counter32, Unit.FromSymbol("B")),
    ];

    private static SourceBinding Binding() => new(
        ScopeId,
        SourceId,
        SourceBindingGeneration.From(1),
        SourceSessionGeneration.From(1));

    private static SnmpOid Oid(string value) => SnmpOid.Parse(value, 32, 128).Value;

    private static byte[] Response(
        int requestId,
        string community,
        int errorStatus,
        params byte[][] variables) =>
        Tlv(0x30, Concat(
            Tlv(0x02, [1]),
            Tlv(0x04, Encoding.UTF8.GetBytes(community)),
            Tlv(0xA2, Concat(
                Tlv(0x02, PositiveInteger(requestId)),
                Tlv(0x02, PositiveInteger(errorStatus)),
                Tlv(0x02, [0]),
                Tlv(0x30, Concat(variables))))));

    private static byte[] Variable(SnmpOid oid, byte tag, byte[] value) =>
        Tlv(0x30, Concat(Tlv(0x06, EncodeOid(oid)), Tlv(tag, value)));

    private static byte[] EncodeOid(SnmpOid oid)
    {
        var arcs = oid.Value.Split('.').Select(uint.Parse).ToArray();
        var bytes = new List<byte>();
        AppendBase128(bytes, checked((ulong)(arcs[0] * 40) + arcs[1]));
        foreach (var arc in arcs.Skip(2))
        {
            AppendBase128(bytes, arc);
        }

        return bytes.ToArray();
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

    private static byte[] Tlv(byte tag, byte[] value) =>
        value.Length < 0x80
            ? [tag, checked((byte)value.Length), .. value]
            : [tag, 0x82, checked((byte)(value.Length >> 8)), checked((byte)value.Length), .. value];

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private sealed class FixedClock : IWallClock, IMonotonicClock
    {
        public DateTimeOffset GetUtcNow() => Now;
        public MonotonicTimestamp GetTimestamp() => new(0);
        public TimeSpan GetElapsedTime(MonotonicTimestamp start, MonotonicTimestamp finish) => TimeSpan.Zero;
    }

    private sealed class MutableSecretResolver : IProtocolSecretResolver
    {
        private string value;

        public MutableSecretResolver(string value) => this.value = value;

        public void Replace(string replacement) => value = replacement;

        public ValueTask<ProtocolSecretLease> ResolveAsync(
            ProtocolSecretReference reference,
            ProtocolWorkloadIdentity workloadIdentity,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ProtocolSecretLease.Create(value));
    }

    private sealed class QueueClientFactory : ISnmpDatagramClientFactory
    {
        private readonly Queue<ISnmpDatagramClient> clients;

        public QueueClientFactory(params ISnmpDatagramClient[] clients) =>
            this.clients = new Queue<ISnmpDatagramClient>(clients);

        public int OpenAttempts { get; private set; }

        public ValueTask<ISnmpDatagramClient> OpenAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            OpenAttempts++;
            return ValueTask.FromResult(clients.Dequeue());
        }
    }

    private sealed class StaticClient : ISnmpDatagramClient
    {
        private readonly ReadOnlyMemory<byte> response;

        public StaticClient(ReadOnlyMemory<byte> response) => this.response = response;

        public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> request,
            int maxResponseBytes,
            CancellationToken cancellationToken) => ValueTask.FromResult(response);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingClient : ISnmpDatagramClient
    {
        private readonly List<byte[]> requests;
        private readonly ReadOnlyMemory<byte> response;

        public CapturingClient(List<byte[]> requests, ReadOnlyMemory<byte> response)
        {
            this.requests = requests;
            this.response = response;
        }

        public ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> request,
            int maxResponseBytes,
            CancellationToken cancellationToken)
        {
            requests.Add(request.ToArray());
            return ValueTask.FromResult(response);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TimeoutClient : ISnmpDatagramClient
    {
        public async ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> request,
            int maxResponseBytes,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ReadOnlyMemory<byte>.Empty;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
