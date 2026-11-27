using System.Net;
using System.Net.Sockets;
using RasaMic.Audio;
using RasaMic.Client.Core;
using RasaMic.Protocol;
using RasaMic.Server;
using Xunit;

namespace RasaMic.IntegrationTests;

public class LoopbackTests
{
    private sealed class CollectBackend : IVirtualMicBackend
    {
        private readonly List<float> _samples = new();
        private readonly List<long> _writeTimestamps = new();
        private float _peak;
        public string Name => "collect";
        public int Count { get { lock (_samples) return _samples.Count; } }
        public float Peak { get { lock (_samples) return _peak; } }
        public double WriteSpanMs { get { lock (_samples) return _writeTimestamps.Count < 2 ? 0 : (_writeTimestamps[^1] - _writeTimestamps[0]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; } }
        public void Start(int sampleRate, int channels) { }
        public void Write(ReadOnlySpan<float> pcm)
        {
            lock (_samples)
            {
                _writeTimestamps.Add(System.Diagnostics.Stopwatch.GetTimestamp());
                foreach (float s in pcm)
                {
                    _samples.Add(s);
                    _peak = Math.Max(_peak, Math.Abs(s));
                }
            }
        }
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

    private static void FillSine(short[] frame, int frameIndex)
    {
        for (int s = 0; s < frame.Length; s++)
            frame[s] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * (frameIndex * frame.Length + s) / 48000.0));
    }

    private static async Task<(int Sent, CollectBackend Backend)> RunSession(int dropEvery, CancellationToken ct, ControlMessage? preFeed = null, PipelineOptions? options = null, bool tcpAudio = false, int gapAfterFrame = 0, int gapMs = 0, bool audioEncrypted = true, int frameCount = 200, int sendDelayMs = 5, int waitAfterMs = 1200)
    {
        int port = FreePort();
        using var identity = ServerIdentity.Create();
        var backend = new CollectBackend();
        using var server = new RasaMicServer(identity, backend, port, audioEncrypted: audioEncrypted);
        var serverTask = server.RunAsync(ct);
        await WaitListening(server, ct);

        using var client = new ClientSession();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), null, "loopback-test", ct, tcpAudio);
        Assert.Equal(audioEncrypted, client.AudioEncrypted);
        if (preFeed is not null)
        {
            await client.SendControlAsync(preFeed, ct);
            await Task.Delay(100, ct); // let the server apply it before audio flows
        }

        using var pipeline = new AudioPipeline(options ?? new PipelineOptions());
        short[] frame = new short[480];
        byte[] packet = new byte[512];
        int sent = 0;
        int produced = 0;
        for (int i = 0; i < frameCount; i++)
        {
            if (gapMs > 0 && i == gapAfterFrame)
                await Task.Delay(gapMs, ct); // simulated Wi-Fi pull
            FillSine(frame, i);
            pipeline.Feed(frame);
            int len;
            while ((len = pipeline.ReadPacket(packet, out uint seq, out ulong ts)) > 0)
            {
                produced++;
                if (dropEvery > 0 && produced % dropEvery == 0)
                    continue; // simulate loss
                client.SendAudio(packet.AsSpan(0, len), seq, ts);
                sent++;
            }
            if (sendDelayMs > 0)
                await Task.Delay(sendDelayMs, ct);
        }

        await Task.Delay(waitAfterMs, ct);
        await client.SendControlAsync(new Bye("test-done"), ct);
        await Task.Delay(200, ct);
        return (sent, backend);
    }

    [Fact]
    public async Task FullLoopback_EncodesSealsDecodes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        (int sent, CollectBackend backend) = await RunSession(dropEvery: 0, cts.Token);
        Assert.True(sent >= 190, $"only {sent} packets sent");
        // 200 frames x 480 samples = 96000; allow for jitter-buffer tail still held
        Assert.True(backend.Count > 80000, $"only {backend.Count} samples decoded");
    }

    [Fact]
    public async Task FullLoopback_PlainAudio_Decodes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        (int sent, CollectBackend backend) = await RunSession(
            dropEvery: 0, cts.Token, audioEncrypted: false);
        Assert.True(sent >= 190, $"only {sent} packets sent");
        Assert.True(backend.Count > 80000, $"only {backend.Count} samples decoded");
    }

    [Fact]
    public async Task BurstyInput_IsPlayedAtRealTimeRate()
    {
        var flat = new PipelineOptions { NoiseSuppression = false, Agc = false };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        (_, CollectBackend backend) = await RunSession(
            dropEvery: 0, cts.Token, options: flat, frameCount: 100,
            sendDelayMs: 0, waitAfterMs: 1200);
        Assert.True(backend.Count > 40000, $"only {backend.Count} samples decoded");
        Assert.True(backend.WriteSpanMs >= 700,
            $"100 frames played in {backend.WriteSpanMs:F0} ms instead of real time");
    }

    [Fact]
    public async Task TenPercentLoss_PlcKeepsStreamContinuous()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        (int sent, CollectBackend backend) = await RunSession(dropEvery: 10, cts.Token);
        Assert.True(sent >= 170, $"only {sent} packets sent");
        // PLC fills gaps: decoded sample count should stay near the full 96000
        Assert.True(backend.Count > 80000, $"only {backend.Count} samples decoded under loss");
    }

    [Fact]
    public async Task SetGain_ScalesDecodedAudio()
    {
        // AGC off so the 8000/32768 (~0.244) input amplitude survives the codec.
        var flat = new PipelineOptions { NoiseSuppression = false, Agc = false };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        (_, CollectBackend baseline) = await RunSession(dropEvery: 0, cts.Token, options: flat);
        Assert.InRange(baseline.Peak, 0.20f, 0.30f);

        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        (_, CollectBackend boosted) = await RunSession(dropEvery: 0, cts2.Token, new SetGain(6f), flat);
        Assert.InRange(boosted.Peak, 0.40f, 0.58f); // ~2x = +6 dB
    }

    [Fact]
    public async Task TcpAudio_FramedTransportDecodes()
    {
        // adb-reverse/USB tunnels forward TCP only; audio rides the control connection.
        var flat = new PipelineOptions { NoiseSuppression = false, Agc = false };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        (int sent, CollectBackend backend) = await RunSession(dropEvery: 0, cts.Token, options: flat, tcpAudio: true);
        Assert.True(sent >= 190, $"only {sent} packets sent");
        Assert.True(backend.Count > 80000, $"only {backend.Count} samples decoded over TCP");
        Assert.InRange(backend.Peak, 0.20f, 0.30f);
    }

    [Fact]
    public async Task TcpAudio_PlainTransportDecodes()
    {
        var flat = new PipelineOptions { NoiseSuppression = false, Agc = false };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        (int sent, CollectBackend backend) = await RunSession(
            dropEvery: 0, cts.Token, options: flat, tcpAudio: true, audioEncrypted: false);
        Assert.True(sent >= 190, $"only {sent} packets sent");
        Assert.True(backend.Count > 80000, $"only {backend.Count} samples decoded over plain TCP");
        Assert.InRange(backend.Peak, 0.20f, 0.30f);
    }

    [Fact]
    public async Task FiveSecondGap_SilenceThenAutoRecovery()
    {
        // M2 accept: Wi-Fi pull 5 s → silence → stream resumes without restart.
        var flat = new PipelineOptions { NoiseSuppression = false, Agc = false };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        (int sent, CollectBackend backend) = await RunSession(dropEvery: 0, cts.Token, options: flat, gapAfterFrame: 100, gapMs: 5000);
        Assert.True(sent >= 190, $"only {sent} packets sent");
        Assert.True(backend.Count is > 80000 and <= 120000,
            $"stream did not recover cleanly after the gap: {backend.Count} samples");
        Assert.InRange(backend.Peak, 0.20f, 0.30f);
    }

    [Fact]
    public async Task PlainServer_RejectsClientWithoutPlainCapability()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int port = FreePort();
        using var identity = ServerIdentity.Create();
        using var server = new RasaMicServer(
            identity, new CollectBackend(), port, audioEncrypted: false);
        Task serverTask = server.RunAsync(cts.Token);
        await WaitListening(server, cts.Token);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var stream = tcp.GetStream();
        await ControlFraming.WriteAsync(stream, new Hello(
            ProtocolConstants.Version, "legacy-client", "Legacy", new byte[] { 1 }, 0), cts.Token);
        var error = Assert.IsType<RasaMic.Protocol.Error>(
            await ControlFraming.ReadAsync(stream, cts.Token));
        Assert.Contains("Update Rasa", error.Message);

        cts.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task PinnedPairing_RejectsMismatchedKey()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int port = FreePort();
        using var identity = ServerIdentity.Create();
        using var server = new RasaMicServer(identity, new CollectBackend(), port);
        var serverTask = server.RunAsync(cts.Token);
        await WaitListening(server, cts.Token);

        PairingPayload good = server.PairingPayload;
        byte[] tampered = good.StaticPublicKey;
        tampered[^1] ^= 0xFF;
        var bad = good with { StaticPubBase64Url = System.Buffers.Text.Base64Url.EncodeToString(tampered) };

        using var client = new ClientSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), bad, "attacker", cts.Token));

        cts.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }
    }
}