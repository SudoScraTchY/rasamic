using System.Buffers.Binary;
using System.Net.Sockets;

namespace RasaMic.Core.Usb;

public sealed record UsbmuxDevice(int DeviceId, string SerialNumber, string ConnectionType);

/// <summary>
/// usbmuxd client for iOS USB tunneling (M6). Talks to the local usbmuxd
/// service — Apple Devices / iTunes on Windows (127.0.0.1:27015), native on
/// macOS, distro usbmuxd on Linux. After Connect the socket becomes a raw TCP
/// tunnel to a port on the phone, so the normal Rasa protocol runs unchanged.
/// </summary>
public sealed class UsbmuxClient : IDisposable
{
    public const int DefaultPort = 27015;
    private const uint PlistMessageType = 8;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private uint _tag;
    private bool _tunneling;

    private UsbmuxClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public static async Task<UsbmuxClient> ConnectAsync(string host = "127.0.0.1", int port = DefaultPort, CancellationToken ct = default)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct);
            return new UsbmuxClient(tcp);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<UsbmuxDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        var reply = await RequestAsync(new Dictionary<string, object?>
        {
            ["MessageType"] = "ListDevices",
        }, ct);
        var devices = new List<UsbmuxDevice>();
        if (reply.TryGetValue("DeviceList", out object? listObj) && listObj is IList<object?> list)
        {
            foreach (object? entry in list)
            {
                if (entry is not Dictionary<string, object?> d ||
                    d.GetValueOrDefault("DeviceID") is not long id)
                    continue;
                var props = d.GetValueOrDefault("Properties") as Dictionary<string, object?>;
                devices.Add(new UsbmuxDevice(
                    (int)id,
                    props?.GetValueOrDefault("SerialNumber") as string ?? "",
                    props?.GetValueOrDefault("ConnectionType") as string ?? ""));
            }
        }
        return devices;
    }

    /// <summary>
    /// Opens a TCP tunnel to devicePort on the phone. On success the returned
    /// stream is the raw tunnel (this client must not be used for further
    /// usbmuxd requests; disposing it closes the tunnel).
    /// </summary>
    public async Task<Stream> ConnectToDeviceAsync(int deviceId, int devicePort, CancellationToken ct = default)
    {
        if (_tunneling)
            throw new InvalidOperationException("Already tunneling");
        // usbmuxd expects the port in network byte order, stored as an int.
        int portBE = ((devicePort & 0xFF) << 8) | ((devicePort >> 8) & 0xFF);
        var reply = await RequestAsync(new Dictionary<string, object?>
        {
            ["MessageType"] = "Connect",
            ["DeviceID"] = (long)deviceId,
            ["PortNumber"] = (long)portBE,
        }, ct);
        if (reply.GetValueOrDefault("Number") is not long result || result != 0)
            throw new IOException($"usbmuxd Connect failed (result {reply.GetValueOrDefault("Number")})");
        _tunneling = true;
        return _stream;
    }

    private async Task<Dictionary<string, object?>> RequestAsync(Dictionary<string, object?> message, CancellationToken ct)
    {
        message["ClientVersionString"] = "rasamic-1.0";
        message["ProgName"] = "rasamic";
        byte[] payload = Bplist.Write(message);
        byte[] header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(16 + payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1); // version: plist protocol
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), PlistMessageType);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), ++_tag);
        await _stream.WriteAsync(header, ct);
        await _stream.WriteAsync(payload, ct);
        await _stream.FlushAsync(ct);

        byte[] replyHeader = new byte[16];
        await _stream.ReadExactlyAsync(replyHeader, ct);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(replyHeader);
        if (length < 16 || length > 1024 * 1024)
            throw new InvalidDataException($"Bad usbmuxd frame length {length}");
        byte[] replyPayload = new byte[length - 16];
        await _stream.ReadExactlyAsync(replyPayload, ct);
        if (Bplist.Read(replyPayload) is not Dictionary<string, object?> dict)
            throw new InvalidDataException("usbmuxd reply is not a dict");
        return dict;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _tcp.Dispose();
    }
}