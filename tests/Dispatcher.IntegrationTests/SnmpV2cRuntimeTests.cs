using System.Text;
using Dispatcher.Core;
using Dispatcher.Protocols;
using Dispatcher.Semantics;
using Dispatcher.Snmp;
using Xunit;

namespace Dispatcher.IntegrationTests;

public sealed class SnmpV2cRuntimeTests
{
    private const string Community = "c13-loopback";
    private static readonly RuntimeScopeId ScopeId =
        RuntimeScopeId.From(Guid.Parse("fa000000-0000-0000-0000-000000000001"));
    private static readonly SourceId SourceId =
        SourceId.From(Guid.Parse("fb000000-0000-0000-0000-000000000001"));
    private static readonly ProtocolWorkloadIdentity Workload =
        ProtocolWorkloadIdentity.From("runtime-c13-wire");
    private static readonly ProtocolSecretReference SecretReference =
        ProtocolSecretReference.From("env:C13_LOOPBACK");
    private static readonly SnmpOid OidA =
        SnmpOid.Parse("1.3.6.1.2.1.1.3.0", 32, 128).Value;
    private static readonly SnmpOid OidB =
        SnmpOid.Parse("1.3.6.1.2.1.2.2.1.10.1", 32, 128).Value;

    [Fact]
    public async Task LoopbackPeerReceivesOnlyGetAndReturnsMultipleConfiguredOids()
    {
        await using var peer = new FakeSnmpUdpPeer(
            (request, _, _) =>
                ValueTask.FromResult<byte[]?>(
                    FakeSnmpUdpPeer.Response(
                        request,
                        new (byte, byte[])[]
                        {
                            (0x43, [50]),
                            (0x41, [0, 42]),
                        })));
        using var source = CreateSource(peer.Port, Points(), TimeSpan.FromSeconds(1));

        var result = await source.AcquireAsync(Binding(), 1);

        Assert.True(result.IsSuccess, result.Error?.Code.Value);
        Assert.Equal([50L, 42L], result.Value.Observations.Select(item => item.Value.Value));
        Assert.Equal([0xA0], peer.PduTypes);
    }

    [Fact]
    public async Task WrongRequestIdAndCommunityAreRejectedWithoutSecretDisclosure()
    {
        await using var wrongIdPeer = new FakeSnmpUdpPeer(
            (request, _, _) =>
                ValueTask.FromResult<byte[]?>(
                    FakeSnmpUdpPeer.Response(
                        request,
                        Values(request.Oids.Count),
                        requestId: request.RequestId + 1)));
        using var wrongId = CreateSource(
            wrongIdPeer.Port,
            [Point(1, OidA, SnmpNumericType.TimeTicks)],
            TimeSpan.FromSeconds(1));
        var wrongIdResult = await wrongId.AcquireAsync(Binding(), 1);

        await using var communityPeer = new FakeSnmpUdpPeer(
            (request, _, _) =>
                ValueTask.FromResult<byte[]?>(
                    FakeSnmpUdpPeer.Response(
                        request,
                        Values(request.Oids.Count),
                        community: Encoding.UTF8.GetBytes("wrong-community"))));
        using var wrongCommunity = CreateSource(
            communityPeer.Port,
            [Point(1, OidA, SnmpNumericType.TimeTicks)],
            TimeSpan.FromSeconds(1));
        var communityResult = await wrongCommunity.AcquireAsync(Binding(), 1);

        Assert.Equal("snmp.response_malformed", wrongIdResult.Error?.Code.Value);
        Assert.Equal("protocol.io_failed", communityResult.Error?.Code.Value);
        Assert.DoesNotContain(Community, communityResult.Error?.Message, StringComparison.Ordinal);
        Assert.All(
            wrongIdPeer.PduTypes.Concat(communityPeer.PduTypes),
            pdu => Assert.Equal((byte)0xA0, pdu));
    }

    [Fact]
    public async Task MalformedAndOversizedDatagramsAreRejectedInsideWireLimits()
    {
        await using var malformedPeer = new FakeSnmpUdpPeer(
            (_, _, _) => ValueTask.FromResult<byte[]?>([0x30, 0x82, 0x7f]));
        using var malformed = CreateSource(
            malformedPeer.Port,
            [Point(1, OidA, SnmpNumericType.TimeTicks)],
            TimeSpan.FromSeconds(1));
        var malformedResult = await malformed.AcquireAsync(Binding(), 1);

        await using var oversizedPeer = new FakeSnmpUdpPeer(
            (_, _, _) => ValueTask.FromResult<byte[]?>(new byte[2048]));
        using var oversized = CreateSource(
            oversizedPeer.Port,
            [Point(1, OidA, SnmpNumericType.TimeTicks)],
            TimeSpan.FromSeconds(1),
            maxResponseBytes: 128);
        var oversizedResult = await oversized.AcquireAsync(Binding(), 1);

        Assert.Equal("protocol.io_failed", malformedResult.Error?.Code.Value);
        Assert.Equal("protocol.io_failed", oversizedResult.Error?.Code.Value);
    }

    [Fact]
    public async Task TimeoutRetriesWithinConfiguredAttemptLimitAndRecovers()
    {
        await using var peer = new FakeSnmpUdpPeer(
            (request, number, _) =>
                ValueTask.FromResult<byte[]?>(
                    number == 1
                        ? null
                        : FakeSnmpUdpPeer.Response(
                            request,
                            Values(request.Oids.Count))));
        var configuration = Configuration(
            peer.Port,
            [Point(1, OidA, SnmpNumericType.TimeTicks)])
            with
            {
                RetryPolicy = new SnmpRetryPolicy(
                    2,
                    TimeSpan.FromMilliseconds(40),
                    TimeSpan.Zero),
            };
        using var source = CreateSource(configuration, TimeSpan.FromSeconds(1));

        var result = await source.AcquireAsync(Binding(), 1);

        Assert.True(result.IsSuccess, result.Error?.Code.Value);
        Assert.Equal(2, peer.RequestCount);
        Assert.Equal([0xA0, 0xA0], peer.PduTypes);
    }

    private static SnmpV2cSource CreateSource(
        int port,
        IReadOnlyList<SnmpPointConfiguration> points,
        TimeSpan timeout,
        int maxResponseBytes = 4096) =>
        CreateSource(Configuration(port, points), timeout, maxResponseBytes);

    private static SnmpV2cSource CreateSource(
        SnmpV2cSourceConfiguration configuration,
        TimeSpan timeout,
        int maxResponseBytes = 4096) =>
        SnmpV2cSource.Create(
            configuration,
            new SnmpConfigurationLimits(8, 32, 128),
            new SnmpWireLimits(64, 1024),
            Workload,
            new ProtocolIoLimits(timeout, maxResponseBytes, 8, 1),
            new StaticSecretResolver(),
            new UdpSnmpDatagramClientFactory(),
            new SystemClock(TimeProvider.System)).Value;

    private static SnmpV2cSourceConfiguration Configuration(
        int port,
        IReadOnlyList<SnmpPointConfiguration> points) =>
        new(
            SnmpRuntimeProfile.ProductionV2cReadOnly,
            SourceId,
            "127.0.0.1",
            port,
            SecretReference,
            points,
            new SnmpRetryPolicy(
                1,
                TimeSpan.FromMilliseconds(250),
                TimeSpan.Zero));

    private static IReadOnlyList<SnmpPointConfiguration> Points() =>
    [
        Point(1, OidA, SnmpNumericType.TimeTicks),
        Point(2, OidB, SnmpNumericType.Counter32),
    ];

    private static SnmpPointConfiguration Point(
        int number,
        SnmpOid oid,
        SnmpNumericType type) =>
        new(
            PointId.From(Guid.Parse($"fc000000-0000-0000-0000-{number:D12}")),
            oid,
            type,
            Unit.FromSymbol("s"));

    private static SourceBinding Binding() =>
        new(
            ScopeId,
            SourceId,
            SourceBindingGeneration.From(1),
            SourceSessionGeneration.From(1));

    private static (byte Tag, byte[] Value)[] Values(int count) =>
        Enumerable.Range(0, count)
            .Select(_ => ((byte)0x43, new byte[] { 42 }))
            .ToArray();

    private sealed class StaticSecretResolver : IProtocolSecretResolver
    {
        public ValueTask<ProtocolSecretLease> ResolveAsync(
            ProtocolSecretReference reference,
            ProtocolWorkloadIdentity workloadIdentity,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(ProtocolSecretLease.Create(Community));
    }
}
