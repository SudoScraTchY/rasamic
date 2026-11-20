using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RasaMic.Audio;
using RasaMic.Client.Core;
using RasaMic.Protocol;
using RasaMic.Server;
using Xunit;

namespace RasaMic.IntegrationTests;

public class HotPathAllocationTests
{
    private sealed class NullBackend : IVirtualMicBackend
    {
        public string Name => "null";
        public void Start(int sampleRate, int channels) { }
        public void Write(ReadOnlySpan<float> pcm) { }
        public void Stop() { }
        public void Dispose() { }
    }

    // Plan verification: hot-path zero-allocation asserted via
    // GC.GetAllocatedBytesForCurrentThread. Default 5 s for CI; set
    // RASA_ALLOC_BENCH_SECONDS=60 for the full bench.
    [Fact]
    public async Task SenderHotPath_ZeroAllocations()
    {
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("RASA_ALLOC_BENCH_SECONDS"), out int s) && s > 0 ? s : 5;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 30));
        CancellationToken ct = cts.Token;

        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        using var identity = ServerIdentity.Create();
        using var server = new RasaMicServer(identity, new NullBackend(), port);
        Task serverTask = server.RunAsync(ct);
        while (!server.IsListening) await Task.Delay(10, ct);

        using var client = new ClientSession();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), null, "alloc-bench", ct);
        using var pipeline = new AudioPipeline(new PipelineOptions());

        var frame = new short[480];
        var feeder = new Thread(() =>
        {
            int i = 0;
            var sw = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                for (int k = 0; k < frame.Length; k++)
                    frame[k] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * (i * frame.Length + k) / 48000.0));
                pipeline.Feed(frame);
                i++;
                int dueMs = i * 10;
                int left = dueMs - (int)sw.ElapsedMilliseconds;
                if (left > 0) Thread.Sleep(left);
            }
        }) { IsBackground = true, Name = "alloc-feeder" };

        long allocDelta = -1;
        long sent = 0;
        var sender = new Thread(() =>
        {
            byte[] packet = new byte[512];
            // Warm up JIT and the socket path before measuring.
            for (int w = 0; w < 20; w++)
            {
                int len;
                while ((len = pipeline.ReadPacket(packet, out uint seq0, out ulong ts0)) > 0)
                    client.SendAudio(packet.AsSpan(0, len), seq0, ts0);
                Thread.Sleep(5);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            while (!ct.IsCancellationRequested)
            {
                int len = pipeline.ReadPacket(packet, out uint seq, out ulong ts);
                if (len > 0)
                {
                    client.SendAudio(packet.AsSpan(0, len), seq, ts);
                    Interlocked.Increment(ref sent);
                }
                else
                {
                    Thread.Sleep(1);
                }
            }
            Interlocked.Exchange(ref allocDelta, GC.GetAllocatedBytesForCurrentThread() - before);
        }) { IsBackground = true, Name = "alloc-sender" };

        feeder.Start();
        sender.Start();
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        cts.Cancel();
        sender.Join(2000);
        feeder.Join(2000);
        try { await serverTask; } catch (OperationCanceledException) { }

        Assert.True(Interlocked.Read(ref sent) > seconds * 80, $"only {sent} packets sent in {seconds}s");
        Assert.Equal(0, Interlocked.Read(ref allocDelta));
    }
}