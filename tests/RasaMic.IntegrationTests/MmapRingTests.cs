using RasaMic.Server.Backends;
using Xunit;

namespace RasaMic.IntegrationTests;

public class MmapRingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rasa-ring-{Guid.NewGuid():N}.bin");

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    private static float[] Ramp(int frames, int start = 0)
    {
        var pcm = new float[frames];
        for (int i = 0; i < frames; i++)
            pcm[i] = (start + i) / 1000f;
        return pcm;
    }

    [Fact]
    public void Roundtrip_WriteThenRead_ReturnsSameSamples()
    {
        using var writer = new MmapRingWriter(_path, 48000, 1, 1024);
        using var reader = new MmapRingReader(_path);
        float[] sent = Ramp(480);
        writer.Write(sent);
        var got = new float[480];
        Assert.Equal(480, reader.Read(got));
        Assert.Equal(sent, got);
        Assert.Equal(48000, reader.SampleRate);
        Assert.Equal(1, reader.Channels);
    }

    [Fact]
    public void Wraparound_CrossesCapacityBoundary()
    {
        using var writer = new MmapRingWriter(_path, 48000, 1, 1024);
        using var reader = new MmapRingReader(_path);
        writer.Write(Ramp(1000));
        var drain = new float[1000];
        Assert.Equal(1000, reader.Read(drain));
        float[] sent = Ramp(500, 1000);
        writer.Write(sent); // wraps past frame 1024
        var got = new float[500];
        Assert.Equal(500, reader.Read(got));
        Assert.Equal(sent, got);
    }

    [Fact]
    public void Underrun_ReturnsSilence()
    {
        using var writer = new MmapRingWriter(_path, 48000, 1, 1024);
        using var reader = new MmapRingReader(_path);
        var got = new float[480];
        Array.Fill(got, 9f);
        Assert.Equal(0, reader.Read(got));
        Assert.All(got, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void PartialRead_FillsRemainderWithSilence()
    {
        using var writer = new MmapRingWriter(_path, 48000, 1, 1024);
        using var reader = new MmapRingReader(_path);
        writer.Write(Ramp(100));
        var got = new float[480];
        Assert.Equal(100, reader.Read(got));
        Assert.Equal(Ramp(100), got[..100]);
        Assert.All(got[100..], s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Overflow_ReaderDropsOldestFrames()
    {
        using var writer = new MmapRingWriter(_path, 48000, 1, 1024);
        using var reader = new MmapRingReader(_path);
        writer.Write(Ramp(2000)); // laps the reader
        var got = new float[1024];
        Assert.Equal(1024, reader.Read(got));
        Assert.Equal(Ramp(1024, 976), got); // newest 1024 frames survive
    }

    [Fact]
    public void Stereo_InterleavedRoundtrip()
    {
        using var writer = new MmapRingWriter(_path, 48000, 2, 1024);
        using var reader = new MmapRingReader(_path);
        var sent = new float[960];
        for (int i = 0; i < 480; i++)
        {
            sent[2 * i] = i / 100f;
            sent[2 * i + 1] = -i / 100f;
        }
        writer.Write(sent);
        var got = new float[960];
        Assert.Equal(480, reader.Read(got));
        Assert.Equal(sent, got);
    }

    [Fact]
    public void Drift_OverfullRing_DropsOneSampleThenRateLimits()
    {
        using var writer = new MmapRingWriter(_path, 48000, 1, 4096, compensateDrift: true);
        using var reader = new MmapRingReader(_path);
        // Reader stalls: fill passes target(960)+band(480); first slip is immediate.
        for (int i = 0; i < 5; i++)
            writer.Write(Ramp(480, i * 480));
        Assert.Equal(1, writer.SamplesDropped);
        // Non-emergency slips are rate-limited to one per 10 s.
        writer.Write(Ramp(480, 2400));
        Assert.Equal(1, writer.SamplesDropped);
        Assert.Equal(0, writer.SamplesRepeated);
    }

    [Fact]
    public void Drift_StarvingRing_RepeatsLastFrame()
    {
        using var writer = new MmapRingWriter(_path, 48000, 1, 4096, compensateDrift: true);
        using var reader = new MmapRingReader(_path);
        writer.Write(Ramp(480));
        var drain = new float[479];
        Assert.Equal(479, reader.Read(drain)); // fill = 1, far below target
        writer.Write(Ramp(480, 480));
        Assert.Equal(1, writer.SamplesRepeated);
        // The duplicated frame is the previous last frame, then the new audio follows.
        var got = new float[482];
        Assert.Equal(482, reader.Read(got));
        Assert.Equal(479 / 1000f, got[0]);
        Assert.Equal(479 / 1000f, got[1]); // repeated frame
        Assert.Equal(Ramp(480, 480), got[2..482]);
    }

    [Fact]
    public void MacHalBackend_WriteReachesRing()
    {
        var backend = new MacHalBackend(_path);
        backend.Start(48000, 1);
        try
        {
            using var reader = new MmapRingReader(_path);
            float[] sent = Ramp(480);
            backend.Write(sent);
            var got = new float[480];
            Assert.Equal(480, reader.Read(got));
            Assert.Equal(sent, got);
        }
        finally
        {
            backend.Dispose();
        }
    }
}