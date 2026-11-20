using System.Net;
using System.Net.Sockets;
using RasaMic.Client.Core;
using RasaMic.Protocol;
using RasaMic.Server;
using Xunit;

namespace RasaMic.IntegrationTests;

public class SupervisorTests
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

    [Fact]
    public async Task Supervisor_ReconnectsAfterServerRestart()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        int port = FreePort();
        using var identity = ServerIdentity.Create();
        var backend = new NullBackend();

        using var server1 = new RasaMicServer(identity, backend, port);
        using var server1Cts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var serverTask1 = server1.RunAsync(server1Cts.Token);
        await WaitListening(server1, cts.Token);

        using var initial = new ClientSession();
        await initial.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), null, "sup-test", cts.Token);
        PairingPayload p = server1.PairingPayload;
        var known = new KnownServer(p.ServerId, p.ServerName, p.StaticPubBase64Url, new[] { "127.0.0.1" }, p.Port);

        string storePath = Path.Combine(Path.GetTempPath(), $"rasamic-sup-{Guid.NewGuid():N}.json");
        try
        {
            using var supervisor = new SessionSupervisor(new KnownServerStore(storePath), "sup-test");
            int connected = 0;
            var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            supervisor.StateChanged += s =>
            {
                if (s == "connected" && Interlocked.Increment(ref connected) >= 2)
                    reconnected.TrySetResult();
            };
            Task supTask = supervisor.RunAsync(initial, known, cts.Token);

            // Simulate PC reboot / Wi-Fi pull: server goes away, then returns on the same port.
            server1Cts.Cancel();
            try { await serverTask1; } catch (OperationCanceledException) { }
            server1.Dispose();

            using var server2 = new RasaMicServer(identity, backend, port);
            var serverTask2 = server2.RunAsync(cts.Token);
            await WaitListening(server2, cts.Token);

            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(30), cts.Token);
            Assert.NotNull(supervisor.Session);
            Assert.True(supervisor.Session!.IsConnected);
            Assert.Equal(identity.ServerId, supervisor.Session.ServerId);
            Assert.Contains(new KnownServerStore(storePath).Load(), s => s.ServerId == identity.ServerId);

            cts.Cancel();
            await supTask;
            try { await serverTask2; } catch (OperationCanceledException) { }
        }
        finally
        {
            File.Delete(storePath);
        }
    }
}