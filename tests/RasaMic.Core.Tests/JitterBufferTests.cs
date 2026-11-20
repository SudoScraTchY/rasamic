using RasaMic.Core.Jitter;
using Xunit;

namespace RasaMic.Core.Tests;

public class JitterBufferTests
{
    private static byte[] Payload(int n) => new byte[n];

    [Fact]
    public void InOrder_EmitsPackets()
    {
        var jb = new JitterBuffer(targetPackets: 2);
        jb.Push(10, Payload(40));
        Assert.Equal(JitterResult.Empty, jb.Next(out _, out _, out _));
        jb.Push(11, Payload(40));
        Assert.Equal(JitterResult.Packet, jb.Next(out uint s1, out _, out int l1));
        Assert.Equal(10u, s1);
        Assert.Equal(40, l1);
        Assert.Equal(JitterResult.Packet, jb.Next(out uint s2, out _, out _));
        Assert.Equal(11u, s2);
    }

    [Fact]
    public void OutOfOrder_Reorders()
    {
        var jb = new JitterBuffer(targetPackets: 3);
        jb.Push(5, Payload(10));
        jb.Push(7, Payload(10));
        jb.Push(6, Payload(10));
        Assert.Equal(JitterResult.Packet, jb.Next(out uint s, out _, out _));
        Assert.Equal(5u, s);
        Assert.Equal(JitterResult.Packet, jb.Next(out s, out _, out _));
        Assert.Equal(6u, s);
        Assert.Equal(JitterResult.Packet, jb.Next(out s, out _, out _));
        Assert.Equal(7u, s);
    }

    [Fact]
    public void MissingPacket_EmitsGap()
    {
        var jb = new JitterBuffer(targetPackets: 2);
        jb.Push(0, Payload(10));
        jb.Push(2, Payload(10)); // 1 lost
        Assert.Equal(JitterResult.Packet, jb.Next(out uint s, out _, out _));
        Assert.Equal(0u, s);
        jb.Push(3, Payload(10));
        Assert.Equal(JitterResult.Gap, jb.Next(out _, out _, out _)); // slot for 1 empty
        Assert.Equal(JitterResult.Packet, jb.Next(out s, out _, out _));
        Assert.Equal(2u, s);
    }

    [Fact]
    public void LatePacket_DroppedAndTargetGrows()
    {
        var jb = new JitterBuffer(targetPackets: 1, maxPackets: 4);
        jb.Push(0, Payload(10));
        Assert.Equal(JitterResult.Packet, jb.Next(out _, out _, out _));
        int before = jb.TargetPackets;
        jb.Push(0, Payload(10)); // late duplicate of consumed seq
        Assert.Equal(before + 1, jb.TargetPackets);
    }

    [Fact]
    public void DuplicateWithinWindow_NotDoubleCounted()
    {
        var jb = new JitterBuffer(targetPackets: 2);
        jb.Push(1, Payload(10));
        jb.Push(1, Payload(10));
        jb.Push(2, Payload(10));
        Assert.Equal(2, jb.Depth);
        Assert.Equal(JitterResult.Packet, jb.Next(out _, out _, out _));
        Assert.Equal(JitterResult.Packet, jb.Next(out _, out _, out _));
        Assert.Equal(JitterResult.Empty, jb.Next(out _, out _, out _));
    }

    [Fact]
    public void SeqWraparound_Handled()
    {
        var jb = new JitterBuffer(targetPackets: 2);
        uint max = uint.MaxValue;
        jb.Push(max, Payload(10));
        jb.Push(0, Payload(10));
        Assert.Equal(JitterResult.Packet, jb.Next(out uint s, out _, out _));
        Assert.Equal(max, s);
        Assert.Equal(JitterResult.Packet, jb.Next(out s, out _, out _));
        Assert.Equal(0u, s);
    }

    [Fact]
    public void OversizedPayload_Dropped()
    {
        var jb = new JitterBuffer(targetPackets: 1);
        jb.Push(0, new byte[600]);
        Assert.Equal(JitterResult.Empty, jb.Next(out _, out _, out _));
    }
}