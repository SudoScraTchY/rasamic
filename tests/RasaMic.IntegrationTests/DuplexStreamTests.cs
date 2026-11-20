using RasaMic.Client.Core;
using Xunit;

namespace RasaMic.IntegrationTests;

public class DuplexStreamTests
{
    [Fact]
    public async Task ReadWrite_Roundtrip_IndependentSides()
    {
        var incoming = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        var outgoing = new MemoryStream();
        using var duplex = new DuplexStream(incoming, outgoing);

        byte[] buf = new byte[4];
        int n = await duplex.ReadAsync(buf);
        Assert.Equal(4, n);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buf);

        await duplex.WriteAsync(new byte[] { 9, 8 });
        Assert.Equal(new byte[] { 9, 8 }, outgoing.ToArray());
    }

    [Fact]
    public void Dispose_DisposesBothSides()
    {
        var incoming = new MemoryStream();
        var outgoing = new MemoryStream();
        var duplex = new DuplexStream(incoming, outgoing);
        duplex.Dispose();
        Assert.Throws<ObjectDisposedException>(() => incoming.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => outgoing.WriteByte(1));
    }

    [Fact]
    public async Task CopyToAsync_BridgesBothDirections()
    {
        var a = new MemoryStream(new byte[1000]);
        var b = new MemoryStream();
        using var duplex = new DuplexStream(a, b);
        var sink = new MemoryStream();
        await duplex.CopyToAsync(sink);
        Assert.Equal(1000, sink.Length);
    }
}