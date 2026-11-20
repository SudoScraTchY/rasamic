using RasaMic.Audio;
using Xunit;
using Xunit.Abstractions;

namespace RasaMic.Audio.Tests;

// M7 stereo: interleaved L/R through the real DSP + Opus path, channel integrity gates.
public class StereoPipelineTests
{
    private const float FullScale = 32768f;
    private readonly ITestOutputHelper _output;

    public StereoPipelineTests(ITestOutputHelper output) => _output = output;

    private static short[] StereoSine(int frames, double freqL, double freqR, double amplitude)
    {
        var pcm = new short[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            pcm[i * 2] = (short)(amplitude * Math.Sin(2 * Math.PI * freqL * i / 48000.0));
            pcm[i * 2 + 1] = (short)(amplitude * Math.Sin(2 * Math.PI * freqR * i / 48000.0));
        }
        return pcm;
    }

    private static List<float> EncodeDecodeStereo(PipelineOptions opts, short[] pcm)
    {
        using var pipeline = new AudioPipeline(opts);
        using var decoder = new OpusDecoder(48000, 2);
        var output = new List<float>(pcm.Length + 3840);
        var packet = new byte[512];
        var frame = new float[1920];

        void Drain()
        {
            int len;
            while ((len = pipeline.ReadPacket(packet, out _, out _)) > 0)
            {
                int n = decoder.Decode(packet.AsSpan(0, len), frame);
                Assert.True(n > 0, $"decode failed: {n}");
                for (int i = 0; i < n * 2; i++) output.Add(frame[i]);
            }
        }

        int step = opts.FrameMs * 48 * 2;
        for (int offset = 0; offset < pcm.Length; offset += step)
        {
            int chunk = Math.Min(step, pcm.Length - offset);
            pipeline.Feed(pcm.AsSpan(offset, chunk));
            Drain();
        }
        pipeline.Feed(new short[step * 2]);
        Drain();
        return output;
    }

    private static float[] Deinterleave(List<float> pcm, int ch)
    {
        var x = new float[pcm.Count / 2];
        for (int i = 0; i < x.Length; i++) x[i] = pcm[i * 2 + ch];
        return x;
    }

    private static int AlignDelay(short[] reference, float[] decoded)
    {
        int maxDelay = Math.Min(960, decoded.Length - reference.Length);
        int bestDelay = 0;
        double bestCorr = double.NegativeInfinity;
        for (int delay = 0; delay <= maxDelay; delay += 8)
        {
            double corr = 0;
            for (int i = 0; i < reference.Length; i += 16)
                corr += (double)reference[i] * decoded[delay + i];
            if (corr > bestCorr) { bestCorr = corr; bestDelay = delay; }
        }
        int lo = Math.Max(0, bestDelay - 8), hi = Math.Min(maxDelay, bestDelay + 8);
        for (int delay = lo; delay <= hi; delay++)
        {
            double corr = 0;
            for (int i = 0; i < reference.Length; i += 16)
                corr += (double)reference[i] * decoded[delay + i];
            if (corr > bestCorr) { bestCorr = corr; bestDelay = delay; }
        }
        return bestDelay;
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
    public void Stereo_ChannelIntegrity_SnrAndSeparation()
    {
        const int frames = 48000;
        var pcm = StereoSine(frames, 440, 880, 12000);
        var decoded = EncodeDecodeStereo(new PipelineOptions
        {
            Channels = 2,
            Bitrate = 64000,
            NoiseSuppression = false,
            Agc = false,
        }, pcm);

        var left = Deinterleave(decoded, 0);
        var right = Deinterleave(decoded, 1);
        var refL = new short[frames];
        var refR = new short[frames];
        for (int i = 0; i < frames; i++)
        {
            refL[i] = pcm[i * 2];
            refR[i] = pcm[i * 2 + 1];
        }

        foreach (var (name, reference, channel, own, other) in new[]
        {
            ("L", refL, left, 440.0, 880.0),
            ("R", refR, right, 880.0, 440.0),
        })
        {
            int delay = AlignDelay(reference, channel);
            double signal = 0, error = 0;
            var aligned = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                aligned[i] = channel[delay + i];
                double s = reference[i];
                double e = s - aligned[i] * FullScale;
                signal += s * s;
                error += e * e;
            }
            double snrDb = 10 * Math.Log10(signal / Math.Max(error, 1e-9));
            double sepDb = 10 * Math.Log10(
                GoertzelPower(aligned, own) / Math.Max(GoertzelPower(aligned, other), 1e-12));
            _output.WriteLine($"{name}: SNR {snrDb:F1} dB, {own} Hz vs {other} Hz separation {sepDb:F1} dB (delay {delay})");
            Assert.True(snrDb >= 20, $"{name} SNR {snrDb:F1} dB < 20 dB");
            Assert.True(sepDb >= 20, $"{name} channel separation {sepDb:F1} dB < 20 dB");
        }
    }

    [Fact]
    public void Stereo_20msFrames_NsAgc_Active()
    {
        var rng = new Random(7);
        var pcm = new short[48000]; // 0.5 s interleaved = 25 x 20 ms frames
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (short)rng.Next(-6000, 6000);

        using var pipeline = new AudioPipeline(new PipelineOptions
        {
            Channels = 2,
            FrameMs = 20,
            Bitrate = 64000,
            NoiseSuppression = true,
            Agc = true,
        });
        using var decoder = new OpusDecoder(48000, 2);
        var packet = new byte[512];
        var frame = new float[1920];
        int decodedFrames = 0;

        for (int offset = 0; offset < pcm.Length; offset += 1920)
        {
            pipeline.Feed(pcm.AsSpan(offset, 1920));
            int len;
            while ((len = pipeline.ReadPacket(packet, out _, out _)) > 0)
            {
                int n = decoder.Decode(packet.AsSpan(0, len), frame);
                Assert.Equal(960, n);
                decodedFrames++;
            }
        }

        var (frames, packets, dropped) = pipeline.GetStats();
        _output.WriteLine($"frames {frames}, packets {packets}, dropped {dropped}, decoded {decodedFrames}");
        Assert.Equal(25ul, frames);
        Assert.Equal(25ul, packets);
        Assert.Equal(0ul, dropped);
        Assert.Equal(25, decodedFrames);
    }

    [Fact]
    public void InvalidChannels_Rejected()
    {
        Assert.Throws<InvalidOperationException>(() => new AudioPipeline(new PipelineOptions { Channels = 3 }));
    }
}