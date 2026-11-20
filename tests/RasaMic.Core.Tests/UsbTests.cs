using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using RasaMic.Core.Usb;
using Xunit;

namespace RasaMic.Core.Tests;

public class BplistTests
{
    [Fact]
    public void Roundtrip_MixedTypes()
    {
        var root = new Dictionary<string, object?>
        {
            ["MessageType"] = "Connect",
            ["DeviceID"] = 7L,
            ["PortNumber"] = 50360L,
            ["Big"] = 5_000_000_000L,
            ["Flag"] = true,
            ["Off"] = false,
            ["Nested"] = new Dictionary<string, object?> { ["SerialNumber"] = "ABC123" },
            ["List"] = new List<object?> { 1L, "two", false },
            ["Persian"] = "رسا",
            ["LongString"] = new string('x', 40), // extended length path
        };
        var back = (Dictionary<string, object?>)Bplist.Read(Bplist.Write(root))!;
        Assert.Equal("Connect", back["MessageType"]);
        Assert.Equal(7L, back["DeviceID"]);
        Assert.Equal(50360L, back["PortNumber"]);
        Assert.Equal(5_000_000_000L, back["Big"]);
        Assert.Equal(true, back["Flag"]);
        Assert.Equal(false, back["Off"]);
        Assert.Equal("ABC123", ((Dictionary<string, object?>)back["Nested"]!)["SerialNumber"]);
        var list = (List<object?>)back["List"]!;
        Assert.Equal(new object?[] { 1L, "two", false }, list);
        Assert.Equal("رسا", back["Persian"]);
        Assert.Equal(new string('x', 40), back["LongString"]);
    }

    [Fact]
    public void Roundtrip_ManyKeys_ExtendedContainerLength()
    {
        var root = new Dictionary<string, object?>();
        for (int i = 0; i < 20; i++)
            root[$"key{i}"] = (long)i;
        var back = (Dictionary<string, object?>)Bplist.Read(Bplist.Write(root))!;
        Assert.Equal(20, back.Count);
        Assert.Equal(19L, back["key19"]);
    }

    [Fact]
    public void Read_RejectsGarbage()
    {
        Assert.Throws<InvalidDataException>(() => Bplist.Read(new byte[64]));
    }
}

public class UsbmuxClientTests
{
    [Fact]
    public async Task ListDevices_And_Connect_TunnelBytes()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var conn = await listener.AcceptTcpClientAsync();
            var s = conn.GetStream();

            var listReq = await ReadFrameAsync(s);
            Assert.Equal("ListDevices", listReq["MessageType"]);
            await WriteFrameAsync(s, new Dictionary<string, object?>
            {
                ["DeviceList"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["DeviceID"] = 7L,
                        ["Properties"] = new Dictionary<string, object?>
                        {
                            ["SerialNumber"] = "ABC123",
                            ["ConnectionType"] = "USB",
                        },
                    },
                },
            });

            var connectReq = await ReadFrameAsync(s);
            Assert.Equal("Connect", connectReq["MessageType"]);
            Assert.Equal(7L, connectReq["DeviceID"]);
            Assert.Equal(50360L, connectReq["PortNumber"]); // 47300 in network byte order
            await WriteFrameAsync(s, new Dictionary<string, object?>
            {
                ["MessageType"] = "Result",
                ["Number"] = 0L,
            });

            byte[] buf = new byte[4];
            await s.ReadExactlyAsync(buf);
            await s.WriteAsync(buf); // tunnel echo
        });

        using var mux = await UsbmuxClient.ConnectAsync("127.0.0.1", port);
        var devices = await mux.ListDevicesAsync();
        var device = Assert.Single(devices);
        Assert.Equal(7, device.DeviceId);
        Assert.Equal("ABC123", device.SerialNumber);
        Assert.Equal("USB", device.ConnectionType);

        var tunnel = await mux.ConnectToDeviceAsync(device.DeviceId, 47300);
        byte[] ping = [1, 2, 3, 4];
        await tunnel.WriteAsync(ping);
        byte[] pong = new byte[4];
        await tunnel.ReadExactlyAsync(pong);
        Assert.Equal(ping, pong);
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();
    }

    [Fact]
    public async Task ConnectFailure_Throws()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var conn = await listener.AcceptTcpClientAsync();
            var s = conn.GetStream();
            await ReadFrameAsync(s);
            await WriteFrameAsync(s, new Dictionary<string, object?>
            {
                ["MessageType"] = "Result",
                ["Number"] = 3L, // bad device
            });
        });
        using var mux = await UsbmuxClient.ConnectAsync("127.0.0.1", port);
        await Assert.ThrowsAsync<IOException>(() => mux.ConnectToDeviceAsync(99, 47300));
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();
    }

    private static async Task<Dictionary<string, object?>> ReadFrameAsync(NetworkStream s)
    {
        byte[] header = new byte[16];
        await s.ReadExactlyAsync(header);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)));
        byte[] payload = new byte[length - 16];
        await s.ReadExactlyAsync(payload);
        return (Dictionary<string, object?>)Bplist.Read(payload)!;
    }

    private static async Task WriteFrameAsync(NetworkStream s, Dictionary<string, object?> message)
    {
        byte[] payload = Bplist.Write(message);
        byte[] header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(16 + payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 1);
        await s.WriteAsync(header);
        await s.WriteAsync(payload);
        await s.FlushAsync();
    }
}