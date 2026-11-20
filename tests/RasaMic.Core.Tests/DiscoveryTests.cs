using System.Net;
using System.Net.Sockets;
using RasaMic.Core.Discovery;
using RasaMic.Protocol;
using Xunit;

namespace RasaMic.Core.Tests;

public class DiscoveryTests
{
    [Fact]
    public void BeaconCodec_Roundtrips()
    {
        var beacon = new ServerBeacon(ProtocolConstants.Version, "abc123", "Test PC", 47300);
        ServerBeacon? decoded = BeaconCodec.Decode(BeaconCodec.Encode(beacon));
        Assert.Equal(beacon, decoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"V\":99,\"Id\":\"x\",\"Name\":\"n\",\"Tcp\":47300}")]
    [InlineData("{\"V\":1,\"Id\":\"\",\"Name\":\"n\",\"Tcp\":47300}")]
    [InlineData("{\"V\":1,\"Id\":\"x\",\"Name\":\"n\",\"Tcp\":0}")]
    public void BeaconCodec_RejectsInvalid(string text)
    {
        Assert.Null(BeaconCodec.Decode(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public async Task Broadcaster_ReachesListener()
    {
        // Unicast to loopback on a free port: same code path as LAN broadcast,
        // without depending on broadcast routing in CI.
        var probe = new System.Net.Sockets.UdpClient(AddressFamily.InterNetwork);
        probe.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        probe.Dispose();

        using var listener = new BeaconListener(port);
        var beacon = new ServerBeacon(ProtocolConstants.Version, "id-1", "Beacon PC", 47300);
        using var broadcaster = new BeaconBroadcaster(
            beacon, target: new IPEndPoint(IPAddress.Loopback, port), interval: TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task run = broadcaster.RunAsync(cts.Token);

        ServerBeacon? received = await listener.ReceiveAsync(cts.Token);
        Assert.Equal(beacon, received);
        cts.Cancel();
        await run;
    }
}