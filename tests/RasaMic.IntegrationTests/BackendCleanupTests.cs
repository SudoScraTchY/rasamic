using System.Net;
using System.Net.Sockets;
using RasaMic.Audio;
using RasaMic.Client.Core;
using RasaMic.Protocol;
using RasaMic.Server;
using Xunit;

namespace RasaMic.IntegrationTests;

public class BackendCleanupTests
{
    [Fact]
    public void Dispose_DisposesOwnedBackend()
    {
        var backend = new TrackingBackend();
        using var identity = ServerIdentity.Create();
        var server = new RasaMicServer(identity, backend, 0);

        server.Dispose();

        Assert.Equal(1, backend.Disposes);
    }

    [Fact]
    public async Task WriteFailure_StillStopsBackend()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var backend = new TrackingBackend { ThrowOnWrite = true };
        using var identity = ServerIdentity.Create();
        using var server = new RasaMicServer(identity, backend, port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task run = server.RunAsync(cts.Token);
        while (!server.IsListening)
            await Task.Delay(10, cts.Token);

        using var client = new ClientSession();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), null, "cleanup-test", cts.Token);
        using var pipeline = new AudioPipeline(new PipelineOptions { NoiseSuppression = false, Agc = false });
        pipeline.Feed(new short[480 * 3]);
        byte[] packet = new byte[512];
        while (pipeline.ReadPacket(packet, out uint seq, out ulong ts) > 0)
            client.SendAudio(packet, seq, ts);

        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (Volatile.Read(ref backend.Stops) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, cts.Token);

        Assert.Equal(1, Volatile.Read(ref backend.Stops));
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    private sealed class TrackingBackend : IVirtualMicBackend
    {
        public string Name => "tracking";
        public bool ThrowOnWrite { get; init; }
        public int Stops;
        public int Disposes;

        public void Start(int sampleRate, int channels) { }
        public void Write(ReadOnlySpan<float> pcm)
        {
            if (ThrowOnWrite)
                throw new InvalidOperationException("write failed");
        }
        public void Stop() => Interlocked.Increment(ref Stops);
        public void Dispose() => Interlocked.Increment(ref Disposes);
    }
}
