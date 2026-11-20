using System.Net;
using System.Net.Sockets;
using RasaMic.Client.Core;
using RasaMic.Core.Discovery;
using RasaMic.Protocol;
using RasaMic.Server;
using Xunit;

namespace RasaMic.IntegrationTests;

public class ReconnectTests
{
    private sealed class NullBackend : IVirtualMicBackend
    {
        public string Name => "null";
        public void Start(int sampleRate, int channels) { }
        public void Write(ReadOnlySpan<float> pcm) { }
        public void Stop() { }
        public void Dispose() { }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static async Task WaitListening(RasaMicServer server, CancellationToken ct)
    {
        while (!server.IsListening)
            await Task.Delay(20, ct);
    }

    private static KnownServer KnownFrom(RasaMicServer server, string[] lastIps)
    {
        PairingPayload p = server.PairingPayload;
        return new KnownServer(p.ServerId, p.ServerName, p.StaticPubBase64Url, lastIps, p.Port);
    }

    [Fact]
    public void Store_RemembersAndUpsertsByServerId()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rasamic-known-{Guid.NewGuid():N}.json");
        try
        {
            var store = new KnownServerStore(path);
            Assert.Empty(store.Load());

            store.Remember(new KnownServer("srv-a", "A", "pk-a", new[] { "192.168.1.10" }, 47300));
            store.Remember(new KnownServer("srv-b", "B", "pk-b", new[] { "192.168.1.11" }, 47300));
            store.Remember(new KnownServer("srv-a", "A renamed", "pk-a", new[] { "192.168.1.20" }, 47301));

            List<KnownServer> all = new KnownServerStore(path).Load();
            Assert.Equal(2, all.Count);
            KnownServer a = Assert.Single(all, s => s.ServerId == "srv-a");
            Assert.Equal("A renamed", a.Name);
            Assert.Equal(new[] { "192.168.1.20" }, a.LastIps);
            Assert.Equal(47301, a.Port);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Find_LastKnownIp_HitsFirstRung()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int port = FreePort();
        using var identity = ServerIdentity.Create();
        using var server = new RasaMicServer(identity, new NullBackend(), port);
        var serverTask = server.RunAsync(cts.Token);
        await WaitListening(server, cts.Token);

        KnownServer known = KnownFrom(server, new[] { "127.0.0.1" });
        IPEndPoint? ep = await ServerFinder.FindAsync(known, cts.Token,
            mdnsTimeout: TimeSpan.FromMilliseconds(100), beaconTimeout: TimeSpan.FromMilliseconds(100));

        Assert.NotNull(ep);
        Assert.Equal(IPAddress.Loopback, ep!.Address);
        Assert.Equal(port, ep.Port);

        cts.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Find_StaleIp_FallsThroughToSubnetProbe()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        int port = FreePort();
        using var identity = ServerIdentity.Create();
        using var server = new RasaMicServer(identity, new NullBackend(), port);
        var serverTask = server.RunAsync(cts.Token);
        await WaitListening(server, cts.Token);

        // TEST-NET-1 (RFC 5737): unroutable, so the first rung genuinely fails.
        // 127.0.0.99 would NOT be stale here: the server binds Any and all of 127/8 is loopback.
        KnownServer known = KnownFrom(server, new[] { "192.0.2.99" });
        IPEndPoint? ep = await ServerFinder.FindAsync(known, cts.Token,
            mdnsTimeout: TimeSpan.FromMilliseconds(100), beaconTimeout: TimeSpan.FromMilliseconds(100),
            probeSubnet: "127.0.0");

        Assert.NotNull(ep);
        // Server binds Any, so every 127/8 address answers; parallel probes race.
        Assert.Equal(127, ep!.Address.GetAddressBytes()[0]);
        Assert.Equal(port, ep.Port);

        cts.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Find_BeaconHit_ReturnsSenderAddress()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int port = FreePort();
        using var identity = ServerIdentity.Create();
        using var server = new RasaMicServer(identity, new NullBackend(), port);
        var serverTask = server.RunAsync(cts.Token);
        await WaitListening(server, cts.Token);

        using var beacon = new BeaconBroadcaster(
            new ServerBeacon(ProtocolConstants.Version, identity.ServerId, "beacon-test", port),
            target: new IPEndPoint(IPAddress.Loopback, ProtocolConstants.BeaconPort),
            interval: TimeSpan.FromMilliseconds(50));
        var beaconTask = beacon.RunAsync(cts.Token);

        KnownServer known = KnownFrom(server, new[] { "192.0.2.99" });
        IPEndPoint? ep = await ServerFinder.FindAsync(known, cts.Token,
            mdnsTimeout: TimeSpan.FromMilliseconds(100), beaconTimeout: TimeSpan.FromSeconds(5),
            probeSubnet: "");

        Assert.NotNull(ep);
        Assert.Equal(IPAddress.Loopback, ep!.Address);
        Assert.Equal(port, ep.Port);

        cts.Cancel();
        try { await beaconTask; } catch (OperationCanceledException) { }
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Find_SubnetProbe_SkipsOwnAddresses()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int port = FreePort();
        IEnumerable<IPAddress> candidates = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        TcpListener? decoy = null;
        IPAddress? own = null;
        foreach (IPAddress a in candidates)
        {
            try
            {
                decoy = new TcpListener(a, port);
                decoy.Start();
                own = a;
                break;
            }
            catch (SocketException) { }
        }
        Assert.NotNull(decoy);
        using (decoy)
        {
            // Stands in for the phone-side adb-reverse listener: a decoy on our own
            // address must never win the subnet probe.
            string ip = own!.ToString();
            var known = new KnownServer("srv-decoy", "X", "pk", new[] { "192.0.2.99" }, port);
            IPEndPoint? ep = await ServerFinder.FindAsync(known, cts.Token,
                mdnsTimeout: TimeSpan.FromMilliseconds(100), beaconTimeout: TimeSpan.FromMilliseconds(100),
                probeSubnet: ip[..ip.LastIndexOf('.')]);

            Assert.Null(ep);
        }
    }

    [Fact]
    public async Task ConnectWithRetry_PinnedHandshake_Succeeds()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int port = FreePort();
        using var identity = ServerIdentity.Create();
        using var server = new RasaMicServer(identity, new NullBackend(), port);
        var serverTask = server.RunAsync(cts.Token);
        await WaitListening(server, cts.Token);

        KnownServer known = KnownFrom(server, new[] { "127.0.0.1" });
        using ClientSession? session = await ServerFinder.ConnectWithRetryAsync(known, "reconnect-test", cts.Token);

        Assert.NotNull(session);
        Assert.True(session!.IsConnected);
        Assert.Equal(identity.ServerId, session.ServerId);

        cts.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ConnectWithRetry_TamperedPin_NeverConnects()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        int port = FreePort();
        using var identity = ServerIdentity.Create();
        using var server = new RasaMicServer(identity, new NullBackend(), port);
        var serverTask = server.RunAsync(cts.Token);
        await WaitListening(server, cts.Token);

        PairingPayload p = server.PairingPayload;
        byte[] tampered = p.StaticPublicKey;
        tampered[^1] ^= 0xFF;
        var known = new KnownServer(p.ServerId, p.ServerName,
            System.Buffers.Text.Base64Url.EncodeToString(tampered), new[] { "127.0.0.1" }, p.Port);

        ClientSession? session = await ServerFinder.ConnectWithRetryAsync(known, "attacker", cts.Token);
        Assert.Null(session);

        cts.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }
    }
}