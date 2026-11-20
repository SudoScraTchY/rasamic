using System.Net;
using System.Net.Sockets;
using RasaMic.Client.Core;
using RasaMic.Protocol;
using RasaMic.Server;
using Xunit;

namespace RasaMic.IntegrationTests;

public class MultiClientTests
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

    private static async Task<RasaMicServer> StartServer(int port, CancellationToken ct)
    {
        var identity = ServerIdentity.Create();
        var server = new RasaMicServer(identity, new NullBackend(), port);
        _ = server.RunAsync(ct);
        while (!server.IsListening)
            await Task.Delay(20, ct);
        return server;
    }

    [Fact]
    public async Task SecondClient_RefusedBusy_FirstKeepsSession()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int port = FreePort();
        using var server = await StartServer(port, cts.Token);

        using var first = new ClientSession();
        await first.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), null, "first", cts.Token);

        using var second = new ClientSession();
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            second.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), null, "second", cts.Token));
        Assert.Contains("Busy", ex.Message);
        Assert.True(first.IsConnected);

        await first.SendControlAsync(new Bye("test-done"), cts.Token);
        await Task.Delay(200, cts.Token);
    }

    [Fact]
    public async Task SlotFreed_AfterDisconnect_NextClientConnects()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int port = FreePort();
        using var server = await StartServer(port, cts.Token);

        var first = new ClientSession();
        await first.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), null, "first", cts.Token);
        await first.SendControlAsync(new Bye("test-done"), cts.Token);
        await Task.Delay(300, cts.Token);
        first.Dispose();

        using var second = new ClientSession();
        await second.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), null, "second", cts.Token);
        Assert.True(second.IsConnected);
        await second.SendControlAsync(new Bye("test-done"), cts.Token);
        await Task.Delay(200, cts.Token);
    }
}