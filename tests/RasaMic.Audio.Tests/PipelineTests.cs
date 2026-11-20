using RasaMic.Audio;
using Xunit;

namespace RasaMic.Audio.Tests;

// The native decoder (opus_decode_float) returns samples normalized to [-1, 1],
// i.e. int16 / 32768. Capture/encode input is int16. Assertions below keep these
// two scales straight: level ratios are scale-free; absolute comparisons convert.
public class PipelineTests
{
    private const float FullScale = 32768f;

    private static short[] Sine(int samples, double freq, double amplitude, int start = 0)
    {
        var pcm = new short[samples];
        for (int i = 0; i < samples; i++)
            pcm[i] = (short)(amplitude * Math.Sin(2 * Math.PI * freq * (i + start) / 48000.0));
        return pcm;
    }

    private static double Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (float v in x) sum += (double)v * v;
        return Math.Sqrt(sum / x.Length);
    }

    private static List<float> EncodeDecode(PipelineOptions opts, short[] pcm)
    {
        using var pipeline = new AudioPipeline(opts);
        using var decoder = new OpusDecoder();
        var output = new List<float>(pcm.Length + 1920);
        var packet = new byte[512];
        var frame = new float[960];

        void Drain()
        {
            int len;
            while ((len = pipeline.ReadPacket(packet, out _, out _)) > 0)
            {
                int n = decoder.Decode(packet.AsSpan(0, len), frame);
                Assert.True(n > 0, $"decode failed: {n}");
                for (int i = 0; i < n; i++) output.Add(frame[i]);
            }
        }

        for (int offset = 0; offset < pcm.Length; offset += 480)
        {
            int chunk = Math.Min(480, pcm.Length - offset);
            pipeline.Feed(pcm.AsSpan(offset, chunk));
            Drain();
        }
        // Flush the Opus encoder lookahead so the tail of the input is decoded.
        pipeline.Feed(new short[960]);
        Drain();
        return output;
    }

    [Fact]
    public void OpusRoundtrip_Sine_SnrAbove20dB()
    {
        const int samples = 48000; // 1 s
        var pcm = Sine(samples, 440, 12000);
        var decoded = EncodeDecode(new PipelineOptions
        {
            Bitrate = 64000,
            NoiseSuppression = false,
            Agc = false,
        }, pcm);

        Assert.True(decoded.Count >= samples, $"expected >= {samples} decoded samples, got {decoded.Count}");

        // Align for codec delay (coarse then fine search). Correlation is scale-free.
        int maxDelay = Math.Min(960, decoded.Count - samples);
        int bestDelay = 0;
        double bestCorr = double.NegativeInfinity;
        for (int delay = 0; delay <= maxDelay; delay += 8)
        {
            double corr = 0;
            for (int i = 0; i < samples; i += 16)
                corr += (double)pcm[i] * decoded[delay + i];
            if (corr > bestCorr) { bestCorr = corr; bestDelay = delay; }
        }
        int lo = Math.Max(0, bestDelay - 8), hi = Math.Min(maxDelay, bestDelay + 8);
        for (int delay = lo; delay <= hi; delay++)
        {
            double corr = 0;
            for (int i = 0; i < samples; i += 16)
                corr += (double)pcm[i] * decoded[delay + i];
            if (corr > bestCorr) { bestCorr = corr; bestDelay = delay; }
        }

        double signal = 0, error = 0;
        for (int i = 0; i < samples; i++)
        {
            double s = pcm[i];
            double e = s - decoded[bestDelay + i] * FullScale;
            signal += s * s;
            error += e * e;
        }
        double snrDb = 10 * Math.Log10(signal / Math.Max(error, 1e-9));
        Assert.True(snrDb > 20, $"SNR {snrDb:F1} dB below 20 dB (delay {bestDelay})");
    }

    [Fact]
    public void Rnnoise_AttenuatesWhiteNoise()
    {
        var rng = new Random(42);
        var pcm = new short[48000];
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (short)rng.Next(-6000, 6000);

        var withNs = EncodeDecode(new PipelineOptions { NoiseSuppression = true, Agc = false }, pcm);
        var withoutNs = EncodeDecode(new PipelineOptions { NoiseSuppression = false, Agc = false }, pcm);

        double rmsNs = Rms(withNs.ToArray().AsSpan(4800, Math.Min(withNs.Count, 48000) - 4800));
        double rmsRaw = Rms(withoutNs.ToArray().AsSpan(4800, Math.Min(withoutNs.Count, 48000) - 4800));
        Assert.True(rmsNs < rmsRaw * 0.8, $"RNNoise did not attenuate noise: ns={rmsNs:E3} raw={rmsRaw:E3}");
    }

    [Fact]
    public void Agc_BoostsQuietSignal()
    {
        var pcm = Sine(48000, 440, 500);
        var withAgc = EncodeDecode(new PipelineOptions { NoiseSuppression = false, Agc = true }, pcm);
        var noAgc = EncodeDecode(new PipelineOptions { NoiseSuppression = false, Agc = false }, pcm);

        // Skip AGC/codec convergence (~0.5 s); AGC should raise the steady-state level.
        const int skip = 24000;
        double rmsAgc = Rms(withAgc.ToArray().AsSpan(skip, withAgc.Count - skip));
        double rmsRaw = Rms(noAgc.ToArray().AsSpan(skip, noAgc.Count - skip));
        Assert.True(rmsAgc > rmsRaw * 1.2, $"AGC did not boost quiet signal: agc={rmsAgc:E3} raw={rmsRaw:E3}");
    }

    [Fact]
    public void Mute_ProducesSilence()
    {
        var pcm = Sine(24000, 440, 12000);
        var decoded = EncodeDecode(new PipelineOptions
        {
            NoiseSuppression = false,
            Agc = false,
            Muted = true,
            Dtx = false,
        }, pcm);
        double rms = Rms(decoded.ToArray());
        Assert.True(rms < 50 / FullScale, $"muted output not silent: rms={rms:E3}");
    }

    [Fact]
    public void Gain_ScalesOutput()
    {
        var pcm = Sine(24000, 440, 4000);
        var quiet = EncodeDecode(new PipelineOptions { NoiseSuppression = false, Agc = false, GainDb = 0 }, pcm);
        var loud = EncodeDecode(new PipelineOptions { NoiseSuppression = false, Agc = false, GainDb = 12 }, pcm);
        double rmsQuiet = Rms(quiet.ToArray());
        double rmsLoud = Rms(loud.ToArray());
        Assert.True(rmsLoud > rmsQuiet * 2.5, $"gain +12 dB did not scale: {rmsQuiet:E3} -> {rmsLoud:E3}");
    }

    [Fact]
    public void Stats_CountFramesAndPackets()
    {
        using var pipeline = new AudioPipeline(new PipelineOptions { NoiseSuppression = false, Agc = false });
        var pcm = Sine(48000, 440, 8000);
        var packet = new byte[512];
        int count = 0;
        // Feed one frame at a time and drain, matching the real sender so the
        // packet ring never overflows.
        for (int offset = 0; offset < pcm.Length; offset += 480)
        {
            pipeline.Feed(pcm.AsSpan(offset, 480));
            int len;
            while ((len = pipeline.ReadPacket(packet, out uint seq, out ulong ts)) > 0)
            {
                Assert.Equal((uint)count, seq);
                Assert.True(ts > 0);
                count++;
            }
        }
        var (frames, packets, dropped) = pipeline.GetStats();
        Assert.Equal(100UL, frames);
        Assert.Equal(100UL, packets);
        Assert.Equal(0UL, dropped);
        Assert.Equal(100, count);
    }

    [Fact]
    public void Plc_ProducesFiniteAudio()
    {
        using var decoder = new OpusDecoder();
        var frame = new float[480];
        int n = decoder.Plc(frame);
        Assert.Equal(480, n);
        Assert.All(frame, v => Assert.True(float.IsFinite(v)));
    }
}