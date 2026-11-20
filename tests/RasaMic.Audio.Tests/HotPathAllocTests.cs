using RasaMic.Audio;
using Xunit;

namespace RasaMic.Audio.Tests;

public class HotPathAllocTests
{
    [Fact]
    public void FeedReadPacket_ZeroManagedAllocations()
    {
        using var pipeline = new AudioPipeline(new PipelineOptions());
        var pcm = new short[480];
        for (int i = 0; i < 480; i++)
            pcm[i] = (short)(6000 * Math.Sin(2 * Math.PI * 220 * i / 48000));
        var packet = new byte[2048];

        for (int i = 0; i < 100; i++)
        {
            pipeline.Feed(pcm);
            while (pipeline.ReadPacket(packet, out _, out _) > 0) { }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 6000; i++) // 60 s of 10 ms frames
        {
            pipeline.Feed(pcm);
            while (pipeline.ReadPacket(packet, out _, out _) > 0) { }
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}