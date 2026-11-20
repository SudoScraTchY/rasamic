using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RasaMic.Audio;
using RasaMic.Client.Core;
using RasaMic.Protocol;
using RasaMic.Server;
using Xunit;
using Xunit.Abstractions;

namespace RasaMic.IntegrationTests;

public class StatsTests
{
    private readonly ITestOutputHelper _output;

    public StatsTests(ITestOutputHelper output) => _output = output;

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

    [Fact]
    public async Task Server_EmitsStatsAt1Hz_WithPacketsLossAndRtt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int port = FreePort();
        using var identity = ServerIdentity.Create();
        using var server = new RasaMicServer(identity, new NullBackend(), port);
        var serverTask = server.RunAsync(cts.Token);
        while (!server.IsListening)
            await Task.Delay(20, cts.Token);

        using var session = new ClientSession();
        await session.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), null, "stats-test", cts.Token);

        // Sender: paced 10 ms frames for ~2.6 s so at least two 1 Hz windows close.
        var sender = Task.Run(() =>
        {
            using var pipeline = new AudioPipeline(new PipelineOptions { NoiseSuppression = false, Agc = false });
            var pcm = new short[480];
            var packet = new byte[512];
            var sw = Stopwatch.StartNew();
            int phase = 0;
            while (sw.ElapsedMilliseconds < 2600)
            {
                for (int i = 0; i < 480; i++)
                    pcm[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * (phase + i) / 48000.0));
                phase += 480;
                pipeline.Feed(pcm);
                int len;
                while ((len = pipeline.ReadPacket(packet, out uint seq, out ulong ts)) > 0)
                    session.SendAudio(packet.AsSpan(0, len), seq, ts);
                Thread.Sleep(10);
            }
        }, cts.Token);

        var stats = new List<Stats>();
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 2600)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            readCts.CancelAfter(300);
            try
            {
                ControlMessage? msg = await session.ReadControlAsync(readCts.Token);
                if (msg is Ping p)
                    await session.SendControlAsync(new Pong(p.TsUs), cts.Token);
                else if (msg is Stats s)
                    stats.Add(s);
                else if (msg is null or Bye)
                    break;
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested) { }
        }
        await sender;

        Assert.True(stats.Count >= 2, $"expected >= 2 Stats in 2.6 s, got {stats.Count}");
        foreach (Stats s in stats)
            _output.WriteLine($"pkts={s.Packets} bytes={s.Bytes} loss={s.LossPct:F1}% jitter={s.JitterMs:F1}ms buffer={s.BufferMs:F0}ms rtt={s.RttMs:F1}ms");

        Assert.Contains(stats, s => s.Packets > 0);
        Assert.Contains(stats, s => s.Bytes > 0);
        Assert.All(stats, s => Assert.Equal(0f, s.LossPct));
        Assert.Contains(stats, s => s.RttMs > 0);
        Assert.NotNull(server.LastSessionStats);

        cts.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }
    }
}