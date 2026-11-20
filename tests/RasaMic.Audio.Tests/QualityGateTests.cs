using RasaMic.Audio;
using Xunit;
using Xunit.Abstractions;

namespace RasaMic.Audio.Tests;

// Plan verification gates: SNR/THD per bitrate, RNNoise A/B >= 10 dB noise-floor reduction.
public class QualityGateTests
{
    private const float FullScale = 32768f;
    private readonly ITestOutputHelper _output;

    public QualityGateTests(ITestOutputHelper output) => _output = output;

    private static short[] Sine(int samples, double freq, double amplitude)
    {
        var pcm = new short[samples];
        for (int i = 0; i < samples; i++)
            pcm[i] = (short)(amplitude * Math.Sin(2 * Math.PI * freq * i / 48000.0));
        return pcm;
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
        pipeline.Feed(new short[960]);
        Drain();
        return output;
    }

    private static int AlignDelay(short[] reference, List<float> decoded)
    {
        int maxDelay = Math.Min(960, decoded.Count - reference.Length);
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

    [Theory]
    [InlineData(16000, 12)]
    [InlineData(32000, 15)]
    [InlineData(64000, 20)]
    [InlineData(96000, 25)]
    public void OpusSnr_PerBitrate(int bitrate, double minSnrDb)
    {
        const int samples = 48000;
        var pcm = Sine(samples, 440, 12000);
        var decoded = EncodeDecode(new PipelineOptions
        {
            Bitrate = bitrate,
            NoiseSuppression = false,
            Agc = false,
        }, pcm);
        int delay = AlignDelay(pcm, decoded);

        double signal = 0, error = 0;
        for (int i = 0; i < samples; i++)
        {
            double s = pcm[i];
            double e = s - decoded[delay + i] * FullScale;
            signal += s * s;
            error += e * e;
        }
        double snrDb = 10 * Math.Log10(signal / Math.Max(error, 1e-9));

        var aligned = new float[samples];
        for (int i = 0; i < samples; i++)
            aligned[i] = decoded[delay + i];
        double fund = GoertzelPower(aligned, 440);
        double harmonics = 0;
        for (int h = 2; h <= 6; h++)
            harmonics += GoertzelPower(aligned, 440 * h);
        double thd = Math.Sqrt(harmonics / Math.Max(fund, 1e-12));

        _output.WriteLine($"{bitrate,6} bps: SNR {snrDb:F1} dB, THD {thd * 100:F2}% (delay {delay})");
        Assert.True(snrDb >= minSnrDb, $"{bitrate} bps SNR {snrDb:F1} dB < {minSnrDb} dB");
        Assert.True(thd < 0.10, $"{bitrate} bps THD {thd * 100:F1}% >= 10%");
    }

    [Fact]
    public void LanDefault_Stereo256k_SnrThdGate()
    {
        const int frames = 48000;
        var pcm = new short[frames * 2];
        var refL = new short[frames];
        var refR = new short[frames];
        for (int i = 0; i < frames; i++)
        {
            // Full-length amplitude ramp makes the correlation peak unique
            // (steady pure tones alias every period; a short fade is drowned
            // out by the steady-state energy).
            double ramp = 0.1 + 0.9 * i / frames;
            refL[i] = (short)(12000 * ramp * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
            refR[i] = (short)(12000 * ramp * Math.Sin(2 * Math.PI * 880 * i / 48000.0));
            pcm[i * 2] = refL[i];
            pcm[i * 2 + 1] = refR[i];
        }

        using var pipeline = new AudioPipeline(new PipelineOptions
        {
            Channels = 2,
            Bitrate = 256000,
            NoiseSuppression = false,
            Agc = false,
        });
        using var decoder = new OpusDecoder(48000, 2);
        var decL = new List<float>(frames + 960);
        var decR = new List<float>(frames + 960);
        var packet = new byte[2048];
        var frame = new float[960];

        void Drain()
        {
            int len;
            while ((len = pipeline.ReadPacket(packet, out _, out _)) > 0)
            {
                int n = decoder.Decode(packet.AsSpan(0, len), frame);
                Assert.True(n > 0, $"decode failed: {n}");
                for (int i = 0; i < n * 2; i += 2)
                {
                    decL.Add(frame[i]);
                    decR.Add(frame[i + 1]);
                }
            }
        }

        for (int off = 0; off < pcm.Length; off += 960)
        {
            pipeline.Feed(pcm.AsSpan(off, Math.Min(960, pcm.Length - off)));
            Drain();
        }
        pipeline.Feed(new short[1920]);
        Drain();

        // Pure tones alias under coarse correlation search; pick the delay that
        // maximizes SNR directly (exhaustive over the plausible codec-delay range).
        (int Delay, double Snr) BestSnr(short[] refCh, List<float> decCh)
        {
            int bestD = 0;
            double bestSnr = double.NegativeInfinity;
            for (int d = 250; d <= 400; d++)
            {
                double sig = 0, err = 0;
                for (int i = 4800; i < frames; i += 4)
                {
                    double s = refCh[i];
                    double e = s - decCh[d + i] * FullScale;
                    sig += s * s;
                    err += e * e;
                }
                double snr = 10 * Math.Log10(sig / Math.Max(err, 1e-9));
                if (snr > bestSnr) { bestSnr = snr; bestD = d; }
            }
            return (bestD, bestSnr);
        }

        var (delayL, snrL) = BestSnr(refL, decL);
        var (delayR, snrR) = BestSnr(refR, decR);
        _output.WriteLine($"best-SNR delays: L {delayL} ({snrL:F1} dB), R {delayR} ({snrR:F1} dB)");
        foreach (var (refCh, decCh, freq, label, delay) in new[]
        {
            (refL, decL, 440.0, "L", delayL),
            (refR, decR, 880.0, "R", delayR),
        })
        {
            double signal = 0, error = 0;
            var aligned = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                aligned[i] = decCh[delay + i];
                if (i < 4800) continue; // skip the fade-in region
                double s = refCh[i];
                double e = s - aligned[i] * FullScale;
                signal += s * s;
                error += e * e;
            }
            double snrDb = 10 * Math.Log10(signal / Math.Max(error, 1e-9));
            double fund = GoertzelPower(aligned.AsSpan(4800), freq);
            double harmonics = 0;
            for (int h = 2; h <= 6; h++)
                harmonics += GoertzelPower(aligned.AsSpan(4800), freq * h);
            double thd = Math.Sqrt(harmonics / Math.Max(fund, 1e-12));
            _output.WriteLine($"stereo 256k {label}: SNR {snrDb:F1} dB, THD {thd * 100:F2}% (delay {delay})");
            Assert.True(snrDb >= 25, $"stereo 256k {label} SNR {snrDb:F1} dB < 25 dB");
            Assert.True(thd < 0.10, $"stereo 256k {label} THD {thd * 100:F1}% >= 10%");
        }
    }

    [Fact]
    public void Stereo_IdenticalChannels_SkewCheck()
    {
        const int frames = 24000;
        var pcm = new short[frames * 2];
        var refM = new short[frames];
        for (int i = 0; i < frames; i++)
        {
            double ramp = 0.1 + 0.9 * i / frames;
            refM[i] = (short)(12000 * ramp * Math.Sin(2 * Math.PI * 440 * i / 48000.0));
            pcm[i * 2] = refM[i];
            pcm[i * 2 + 1] = refM[i];
        }
        using var pipeline = new AudioPipeline(new PipelineOptions { Channels = 2, Bitrate = 256000, NoiseSuppression = false, Agc = false });
        using var decoder = new OpusDecoder(48000, 2);
        var decL = new List<float>();
        var decR = new List<float>();
        var packet = new byte[2048];
        var frame = new float[960];
        void Drain()
        {
            int len;
            while ((len = pipeline.ReadPacket(packet, out _, out _)) > 0)
            {
                int n = decoder.Decode(packet.AsSpan(0, len), frame);
                for (int i = 0; i < n * 2; i += 2) { decL.Add(frame[i]); decR.Add(frame[i + 1]); }
            }
        }
        for (int off = 0; off < pcm.Length; off += 960) { pipeline.Feed(pcm.AsSpan(off, Math.Min(960, pcm.Length - off))); Drain(); }
        pipeline.Feed(new short[1920]);
        Drain();
        int dL = AlignDelay(refM, decL);
        int dR = AlignDelay(refM, decR);
        double err = 0;
        for (int i = 0; i < frames; i++) { double e = decL[dL + i] - decR[dR + i]; err += e * e; }
        _output.WriteLine($"identical channels: delayL {dL} delayR {dR} skew {dR - dL}, L-R rms {Math.Sqrt(err / frames):F4}");
    }

    [Fact]
    public void RnNoise_NoiseFloor_ReducedAtLeast10dB()
    {
        var rng = new Random(42);
        var pcm = new short[96000]; // 2 s: RNNoise needs ~0.5 s to converge
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = (short)rng.Next(-6000, 6000);

        var withNs = EncodeDecode(new PipelineOptions { NoiseSuppression = true, Agc = false }, pcm);
        var withoutNs = EncodeDecode(new PipelineOptions { NoiseSuppression = false, Agc = false }, pcm);

        const int skip = 48000; // measure steady state, second half
        double rmsNs = Rms(withNs.ToArray().AsSpan(skip, Math.Min(withNs.Count, 96000) - skip));
        double rmsRaw = Rms(withoutNs.ToArray().AsSpan(skip, Math.Min(withoutNs.Count, 96000) - skip));
        double reductionDb = 20 * Math.Log10(rmsRaw / Math.Max(rmsNs, 1e-9));

        _output.WriteLine($"noise floor: raw {rmsRaw:E3}, NS {rmsNs:E3}, reduction {reductionDb:F1} dB");
        Assert.True(reductionDb >= 10, $"RNNoise A/B reduction {reductionDb:F1} dB < 10 dB gate");
    }

    private static double Rms(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (float v in x) sum += (double)v * v;
        return Math.Sqrt(sum / x.Length);
    }
}