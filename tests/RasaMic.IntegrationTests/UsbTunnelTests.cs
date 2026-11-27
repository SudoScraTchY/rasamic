using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using RasaMic.Audio;
using RasaMic.Client.Core;
using RasaMic.Core.Usb;
using RasaMic.Protocol;
using RasaMic.Server;
using Xunit;

namespace RasaMic.IntegrationTests;

/// <summary>
/// Simulates the iOS USB path: client → mock usbmuxd (plist ListDevices/Connect)
/// → raw TCP tunnel → real RasaMicServer, with framed audio over the tunnel.
/// </summary>
public class UsbTunnelTests
{
    private sealed class CollectBackend : IVirtualMicBackend
    {
        private int _count;
        public string Name => "collect";
        public int Count => Volatile.Read(ref _count);
        public void Start(int sampleRate, int channels) { }
        public void Write(ReadOnlySpan<float> pcm) => Interlocked.Add(ref _count, pcm.Length);
        public void Stop() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task UsbmuxTunnel_EndToEnd_AudioDecodes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        int serverPort;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        serverPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var identity = ServerIdentity.Create();
        var backend = new CollectBackend();
        using var server = new RasaMicServer(identity, backend, serverPort);
        var serverTask = server.RunAsync(ct);
        while (!server.IsListening)
            await Task.Delay(20, ct);

        var muxListener = new TcpListener(IPAddress.Loopback, 0);
        muxListener.Start();
        int muxPort = ((IPEndPoint)muxListener.LocalEndpoint).Port;
        var muxTask = Task.Run(async () =>
        {
            using var phoneSide = await muxListener.AcceptTcpClientAsync(ct);
            var s = phoneSide.GetStream();
            var listReq = await ReadFrameAsync(s, ct);
            Assert.Equal("ListDevices", listReq["MessageType"]);
            await WriteFrameAsync(s, new Dictionary<string, object?>
            {
                ["DeviceList"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["DeviceID"] = 1L,
                        ["Properties"] = new Dictionary<string, object?>
                        {
                            ["SerialNumber"] = "TESTPHONE",
                            ["ConnectionType"] = "USB",
                        },
                    },
                },
            }, ct);
            var connectReq = await ReadFrameAsync(s, ct);
            Assert.Equal("Connect", connectReq["MessageType"]);
            await WriteFrameAsync(s, new Dictionary<string, object?>
            {
                ["MessageType"] = "Result",
                ["Number"] = 0L,
            }, ct);

            // The "phone port" is the real server: bridge bytes both ways.
            using var pcSide = new TcpClient();
            await pcSide.ConnectAsync(IPAddress.Loopback, serverPort, ct);
            var pc = pcSide.GetStream();
            var a = s.CopyToAsync(pc, ct);
            var b = pc.CopyToAsync(s, ct);
            await Task.WhenAny(a, b);
        }, ct);

        using var mux = await UsbmuxClient.ConnectAsync("127.0.0.1", muxPort, ct);
        var devices = await mux.ListDevicesAsync(ct);
        var device = Assert.Single(devices);
        Assert.Equal("USB", device.ConnectionType);
        var tunnel = await mux.ConnectToDeviceAsync(device.DeviceId, serverPort, ct);

        using var client = new ClientSession();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, serverPort), null, "ios-usb-test", ct, tunnel: tunnel);
        Assert.True(client.TcpAudio);
        Assert.Equal(6, client.PairingCode.Length);

        using var pipeline = new AudioPipeline(new PipelineOptions { NoiseSuppression = false, Agc = false });
        short[] frame = new short[480];
        byte[] packet = new byte[512];
        int sent = 0;
        for (int i = 0; i < 200; i++)
        {
            for (int j = 0; j < frame.Length; j++)
                frame[j] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * (i * frame.Length + j) / 48000.0));
            pipeline.Feed(frame);
            int len;
            while ((len = pipeline.ReadPacket(packet, out uint seq, out ulong ts)) > 0)
            {
                client.SendAudio(packet.AsSpan(0, len), seq, ts);
                sent++;
            }
            await Task.Delay(5, ct);
        }
        await Task.Delay(1200, ct);
        await client.SendControlAsync(new Bye("usb-done"), ct);
        await Task.Delay(200, ct);

        Assert.True(sent >= 190, $"only {sent} packets sent");
        Assert.True(backend.Count > 80000, $"only {backend.Count} samples decoded over the usbmuxd tunnel");

        cts.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }
        muxListener.Stop();
    }

    [Fact]
    public async Task UsbBridge_PcDialsPhone_ListenMode_EndToEnd()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        int serverPort = FreePort();
        using var identity = ServerIdentity.Create();
        var backend = new CollectBackend();
        using var server = new RasaMicServer(identity, backend, serverPort);
        var serverTask = server.RunAsync(ct);
        while (!server.IsListening)
            await Task.Delay(20, ct);

        // The iPhone app in USB mode listens; the PC dials in via usbmuxd.
        var phoneListener = new TcpListener(IPAddress.Any, 0);
        phoneListener.Start();
        int phonePort = ((IPEndPoint)phoneListener.LocalEndpoint).Port;

        var muxListener = new TcpListener(IPAddress.Loopback, 0);
        muxListener.Start();
        int muxPort = ((IPEndPoint)muxListener.LocalEndpoint).Port;
        var muxTask = Task.Run(async () =>
        {
            using var pcSide = await muxListener.AcceptTcpClientAsync(ct);
            var s = pcSide.GetStream();
            await ReadFrameAsync(s, ct); // ListDevices
            await WriteFrameAsync(s, new Dictionary<string, object?>
            {
                ["DeviceList"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["DeviceID"] = 1L,
                        ["Properties"] = new Dictionary<string, object?>
                        {
                            ["SerialNumber"] = "TESTPHONE",
                            ["ConnectionType"] = "USB",
                        },
                    },
                },
            }, ct);
            var connectReq = await ReadFrameAsync(s, ct);
            Assert.Equal("Connect", connectReq["MessageType"]);
            await WriteFrameAsync(s, new Dictionary<string, object?>
            {
                ["MessageType"] = "Result",
                ["Number"] = 0L,
            }, ct);
            using var phoneSide = new TcpClient();
            await phoneSide.ConnectAsync(IPAddress.Loopback, phonePort, ct);
            var phone = phoneSide.GetStream();
            await Task.WhenAny(s.CopyToAsync(phone, ct), phone.CopyToAsync(s, ct));
        }, ct);

        var bridgeTask = UsbBridge.RunAsync(serverPort, phonePort, muxPort, ct);

        using var phoneConn = await phoneListener.AcceptTcpClientAsync(ct);
        using var client = new ClientSession();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, phonePort), null, "ios-listen-test", ct, tunnel: phoneConn.GetStream());
        Assert.True(client.TcpAudio);

        using var pipeline = new AudioPipeline(new PipelineOptions { NoiseSuppression = false, Agc = false });
        short[] frame = new short[480];
        byte[] packet = new byte[512];
        int sent = 0;
        for (int i = 0; i < 200; i++)
        {
            for (int j = 0; j < frame.Length; j++)
                frame[j] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * (i * frame.Length + j) / 48000.0));
            pipeline.Feed(frame);
            int len;
            while ((len = pipeline.ReadPacket(packet, out uint seq, out ulong ts)) > 0)
            {
                client.SendAudio(packet.AsSpan(0, len), seq, ts);
                sent++;
            }
            await Task.Delay(5, ct);
        }
        await Task.Delay(1200, ct);
        await client.SendControlAsync(new Bye("usb-done"), ct);
        await Task.Delay(200, ct);

        Assert.True(sent >= 190, $"only {sent} packets sent");
        Assert.True(backend.Count > 80000, $"only {backend.Count} samples decoded via UsbBridge");

        cts.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }
        try { await bridgeTask; } catch (OperationCanceledException) { }
        phoneListener.Stop();
        muxListener.Stop();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static async Task<Dictionary<string, object?>> ReadFrameAsync(NetworkStream s, CancellationToken ct)
    {
        byte[] header = new byte[16];
        await s.ReadExactlyAsync(header, ct);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        byte[] payload = new byte[length - 16];
        await s.ReadExactlyAsync(payload, ct);
        return (Dictionary<string, object?>)Bplist.Read(payload)!;
    }

    private static async Task WriteFrameAsync(NetworkStream s, Dictionary<string, object?> message, CancellationToken ct)
    {
        byte[] payload = Bplist.Write(message);
        byte[] header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(16 + payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 1);
        await s.WriteAsync(header, ct);
        await s.WriteAsync(payload, ct);
        await s.FlushAsync(ct);
    }
}