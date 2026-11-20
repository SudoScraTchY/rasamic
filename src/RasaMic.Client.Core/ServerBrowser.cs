using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using RasaMic.Core.Discovery;
using RasaMic.Protocol;

namespace RasaMic.Client.Core;

public sealed record FoundServer(string Id, string Name, IPAddress Address, int Port);

/// <summary>
/// Continuous LAN browse: UDP beacon plus mDNS, deduped by server id.
/// Found fires once per new server or endpoint change, not per beacon.
/// </summary>
public sealed class ServerBrowser : IDisposable
{
    private readonly ConcurrentDictionary<string, FoundServer> _servers = new();
    private readonly bool _mdns;
    private readonly int _beaconPort;
    private readonly CancellationTokenSource _cts = new();
    private Task? _beaconTask;
    private RasaMdnsBrowser? _browser;

    public ServerBrowser(bool mdns = true, int beaconPort = ProtocolConstants.BeaconPort)
    {
        _mdns = mdns;
        _beaconPort = beaconPort;
    }

    public event Action<FoundServer>? Found;

    public IReadOnlyList<FoundServer> Servers => _servers.Values.ToList();

    public void Start()
    {
        if (_beaconTask is not null)
            return;
        _beaconTask = Task.Run(() => BeaconLoopAsync(_cts.Token));
        if (_mdns)
        {
            _browser = new RasaMdnsBrowser();
            _browser.Found += s => Offer(new FoundServer(s.Id, s.Name, s.Address, s.Port));
            _browser.Start();
        }
    }

    private async Task BeaconLoopAsync(CancellationToken ct)
    {
        try
        {
            using var listener = new BeaconListener(_beaconPort);
            while (!ct.IsCancellationRequested)
            {
                (ServerBeacon Beacon, IPEndPoint Source)? hit =
                    await listener.ReceiveFromAsync(ct).ConfigureAwait(false);
                if (hit is null)
                    break;
                Offer(new FoundServer(hit.Value.Beacon.Id, hit.Value.Beacon.Name,
                    hit.Value.Source.Address, hit.Value.Beacon.Tcp));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { /* beacon port unavailable */ }
    }

    private void Offer(FoundServer s)
    {
        if (_servers.TryGetValue(s.Id, out FoundServer? old) &&
            old.Address.Equals(s.Address) && old.Port == s.Port)
            return;
        _servers[s.Id] = s;
        Found?.Invoke(s);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _browser?.Dispose();
        try { _beaconTask?.Wait(500); } catch (AggregateException) { }
        _cts.Dispose();
    }
}