using System.Net;
using System.Net.Sockets;
using RasaMic.Audio;
using RasaMic.Client.Core;
using RasaMic.Protocol;
using RasaMic.Server;
using Xunit;
using Xunit.Abstractions;

namespace RasaMic.IntegrationTests;

// M7 stereo E2E: stereo pipeline -> encrypted UDP/TCP -> server decode -> backend,
// plus the mono-sink downmix path (Windows driver / macOS HAL are mono-only).
public class StereoLoopbackTests
{
    private readonly ITestOutputHelper _output;

    public StereoLoopbackTests(ITestOutputHelper output) => _output = output;

    private sealed class CollectBackend(bool stereoCapable) : IVirtualMicBackend
    {
        private readonly List<float> _values = new();
        public string Name => "collect";
        public bool StereoCapable { get; } = stereoCapable;
        public int StartChannels { get; private set; }
        public int Count { get { lock (_values) return _values.Count; } }
        public void Start(int sampleRate, int channels) => StartChannels = channels;
        public void Write(ReadOnlySpan<float> pcm)
        {
            lock (_values) _values.AddRange(pcm.ToArray());
        }
        public void Stop() { }
        public void Dispose() { }
        public float[] Snapshot() { lock (_values) return _values.ToArray(); }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static async Task<(int Sent, CollectBackend Backend)> RunStereoSession(bool backendStereo, CancellationToken ct)
    {
        int port = FreePort();
        using var identity = ServerIdentity.Create();
        var backend = new CollectBackend(backendStereo);
        using var server = new RasaMicServer(identity, backend, port);
        var serverTask = server.RunAsync(ct);
        while (!server.IsListening)
            await Task.Delay(20, ct);

        using var client = new ClientSession();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), null, "stereo-test", ct,
            features: ProtocolConstants.FeatureStereo);

        using var pipeline = new AudioPipeline(new PipelineOptions
        {
            Channels = 2,
            Bitrate = 64000,
            NoiseSuppression = false,
            Agc = false,
        });
        short[] frame = new short[960]; // 480 frames interleaved L/R
        byte[] packet = new byte[512];
        int sent = 0;
        for (int i = 0; i < 200; i++)
        {
            for (int s = 0; s < 480; s++)
            {
                int t = i * 480 + s;
                frame[s * 2] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * t / 48000.0));
                frame[s * 2 + 1] = (short)(8000 * Math.Sin(2 * Math.PI * 880 * t / 48000.0));
            }
            pipeline.Feed(frame);
            int len;
            while ((len = pipeline.ReadPacket(packet, out uint seq, out ulong ts)) > 0)
            {
                client.SendAudio(packet.AsSpan(0, len), seq, ts);
                sent++;
            }
            await Task.Delay(5, ct);
        }

        await Task.Delay(600, ct);
        await client.SendControlAsync(new Bye("test-done"), ct);
        await Task.Delay(200, ct);
        return (sent, backend);
    }

    private static double GoertzelPower(ReadOnlySpan<float> x, double freq)
    {
        double w = 2 * Math.PI * freq / 48000.0;
        double coeff = 2 * Math.Cos(w);
        double s0 = 0, s1 = 0, s2 = 0;
        foreach (float v in x)
        {
            s0 = v + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return s1 * s1 + s2 * s2 - coeff * s1 * s2;
    }

    [Fact]
    public async Task StereoSession_BackendReceivesSeparateChannels()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        (int sent, CollectBackend backend) = await RunStereoSession(backendStereo: true, cts.Token);
        Assert.True(sent >= 190, $"only {sent} packets sent");
        Assert.Equal(2, backend.StartChannels);
        var values = backend.Snapshot();
        Assert.True(values.Length > 160000, $"only {values.Length} stereo values decoded");

        int frames = values.Length / 2;
        var left = new float[frames];
        var right = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            left[i] = values[i * 2];
            right[i] = values[i * 2 + 1];
        }
        double sepL = 10 * Math.Log10(GoertzelPower(left, 440) / Math.Max(GoertzelPower(left, 880), 1e-12));
        double sepR = 10 * Math.Log10(GoertzelPower(right, 880) / Math.Max(GoertzelPower(right, 440), 1e-12));
        _output.WriteLine($"stereo E2E: {values.Length} values, L separation {sepL:F1} dB, R separation {sepR:F1} dB");
        Assert.True(sepL >= 15, $"L channel separation {sepL:F1} dB < 15 dB");
        Assert.True(sepR >= 15, $"R channel separation {sepR:F1} dB < 15 dB");
    }

    [Fact]
    public async Task StereoSession_MonoBackend_GetsDownmix()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        (int sent, CollectBackend backend) = await RunStereoSession(backendStereo: false, cts.Token);
        Assert.True(sent >= 190, $"only {sent} packets sent");
        Assert.Equal(1, backend.StartChannels);
        var mono = backend.Snapshot();
        Assert.True(mono.Length > 80000, $"only {mono.Length} mono samples decoded");

        double p440 = GoertzelPower(mono, 440);
        double p880 = GoertzelPower(mono, 880);
        double p2000 = GoertzelPower(mono, 2000);
        _output.WriteLine($"downmix E2E: {mono.Length} samples, 440 Hz {p440:E3}, 880 Hz {p880:E3}, 2 kHz {p2000:E3}");
        Assert.True(p440 > 10 * p2000, "440 Hz tone lost in downmix");
        Assert.True(p880 > 10 * p2000, "880 Hz tone lost in downmix");
    }
}