using System.Net;
using System.Net.Sockets;
using RasaMic.Client.Core;
using RasaMic.Protocol;
using RasaMic.Server;
using Xunit;

namespace RasaMic.IntegrationTests;

public class SupervisorReconnectTests
{
    [Fact]
    public async Task ReconnectFactory_OverridesLadder_UsedAfterLoss()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        using var identity = ServerIdentity.Create();
        using var server = new RasaMicServer(identity, new NullBackend(), port);
        var serverTask = server.RunAsync(ct);
        while (!server.IsListening)
            await Task.Delay(20, ct);

        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        using var first = new ClientSession();
        await first.ConnectAsync(endpoint, null, "supervisor-test", ct);

        string storePath = Path.GetTempFileName();
        try
        {
            using var supervisor = new SessionSupervisor(new KnownServerStore(storePath), "supervisor-test");
            var states = new List<string>();
            supervisor.StateChanged += s => { lock (states) states.Add(s); };
            int factoryCalls = 0;
            var known = new KnownServer(first.ServerId, first.ServerName, first.StaticPubBase64Url,
                Array.Empty<string>(), port, false);
            var run = supervisor.RunAsync(first, known, ct, async token =>
            {
                Interlocked.Increment(ref factoryCalls);
                var next = new ClientSession();
                await next.ConnectAsync(endpoint, null, "supervisor-test", token);
                return next;
            });

            await Task.Delay(300, ct);
            first.Dispose(); // simulate cable pull / transport death

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (Volatile.Read(ref factoryCalls) == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(100, ct);
            Assert.True(factoryCalls >= 1, "reconnect factory was never invoked");
            var reconnectedBy = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < reconnectedBy)
            {
                lock (states)
                {
                    if (states.Contains("reconnecting") && states.Count > 0 && states[^1] == "connected")
                        break;
                }
                await Task.Delay(50, ct);
            }
            lock (states)
            {
                Assert.Contains("reconnecting", states);
                Assert.Equal("connected", states[^1]);
            }
            Assert.NotNull(supervisor.Session);
            Assert.True(supervisor.Session!.IsConnected);

            cts.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }
        finally
        {
            File.Delete(storePath);
        }
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    private sealed class NullBackend : IVirtualMicBackend
    {
        public string Name => "null";
        public void Start(int sampleRate, int channels) { }
        public void Write(ReadOnlySpan<float> pcm) { }
        public void Stop() { }
        public void Dispose() { }
    }
}