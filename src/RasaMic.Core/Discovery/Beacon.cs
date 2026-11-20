using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using RasaMic.Protocol;

namespace RasaMic.Core.Discovery;

public sealed record ServerBeacon(int V, string Id, string Name, int Tcp);

public static class BeaconCodec
{
    public static byte[] Encode(ServerBeacon beacon) => JsonSerializer.SerializeToUtf8Bytes(beacon);

    public static ServerBeacon? Decode(ReadOnlySpan<byte> data)
    {
        try
        {
            var b = JsonSerializer.Deserialize<ServerBeacon>(data);
            return b is { V: ProtocolConstants.Version, Tcp: > 0 and <= 65535 } && b.Id.Length > 0 ? b : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Broadcasts a server beacon every 2 s so clients can find the server without mDNS.</summary>
public sealed class BeaconBroadcaster : IDisposable
{
    private readonly UdpClient _udp = new(AddressFamily.InterNetwork);
    private readonly byte[] _payload;
    private readonly IPEndPoint _target;
    private readonly TimeSpan _interval;

    public BeaconBroadcaster(ServerBeacon beacon, int beaconPort = ProtocolConstants.BeaconPort, IPEndPoint? target = null, TimeSpan? interval = null)
    {
        _udp.EnableBroadcast = true;
        _payload = BeaconCodec.Encode(beacon);
        _target = target ?? new IPEndPoint(IPAddress.Broadcast, beaconPort);
        _interval = interval ?? TimeSpan.FromSeconds(2);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _udp.SendAsync(_payload, _target, ct).ConfigureAwait(false);
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { /* network went away; stop quietly */ }
    }

    public void Dispose() => _udp.Dispose();
}

/// <summary>Listens for server beacons on the LAN.</summary>
public sealed class BeaconListener : IDisposable
{
    private readonly UdpClient _udp;

    public BeaconListener(int port = ProtocolConstants.BeaconPort)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    public async Task<ServerBeacon?> ReceiveAsync(CancellationToken ct) =>
        (await ReceiveFromAsync(ct).ConfigureAwait(false))?.Beacon;

    /// <summary>Like ReceiveAsync but also returns the sender's address (the server's IP).</summary>
    public async Task<(ServerBeacon Beacon, IPEndPoint Source)?> ReceiveFromAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (SocketException) { continue; }
            ServerBeacon? b = BeaconCodec.Decode(r.Buffer);
            if (b is not null)
                return (b, r.RemoteEndPoint);
        }
        return null;
    }

    public void Dispose() => _udp.Dispose();
}