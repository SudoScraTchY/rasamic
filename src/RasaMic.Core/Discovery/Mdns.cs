using System.Net;
using Haukcode.Mdns;
using RasaMic.Protocol;

namespace RasaMic.Core.Discovery;

/// <summary>Advertises _rasamic._udp with TXT v/id/name/tcp/enc (plan discovery contract).</summary>
public sealed class RasaMdnsAdvertiser : IDisposable
{
    private readonly MdnsAdvertiser _advertiser;

    public RasaMdnsAdvertiser(string instanceName, string serverId, int port)
    {
        var txt = new Dictionary<string, string>
        {
            ["v"] = ProtocolConstants.Version.ToString(),
            ["id"] = serverId,
            ["name"] = instanceName,
            ["tcp"] = port.ToString(),
            ["enc"] = "aes256gcm",
        };
        var profile = new ServiceProfile(instanceName, ProtocolConstants.MdnsServiceType, (ushort)port, txt);
        _advertiser = new MdnsAdvertiser(profile);
    }

    public void Start() => _advertiser.Start();

    public void Dispose() => _advertiser.Dispose();
}

public sealed record DiscoveredServer(string Name, string Id, IPAddress Address, int Port);

/// <summary>Browses for Rasa servers; raises Found once per instance with a usable address.</summary>
public sealed class RasaMdnsBrowser : IDisposable
{
    private readonly MdnsBrowser _browser;
    private readonly HashSet<string> _seen = new();

    public event Action<DiscoveredServer>? Found;

    public RasaMdnsBrowser()
    {
        _browser = new MdnsBrowser(ProtocolConstants.MdnsServiceType);
        _browser.ServiceFound += svc =>
        {
            // Loopback A records are unusable remotely and, with adb-reverse
            // active, hairpin the phone back into the PC's own tunnel.
            if (svc.Address is null || svc.Port <= 0 || IPAddress.IsLoopback(svc.Address))
                return;
            svc.Properties.TryGetValue("id", out string? id);
            svc.Properties.TryGetValue("v", out string? v);
            if (v != ProtocolConstants.Version.ToString())
                return;
            lock (_seen)
            {
                if (!_seen.Add(svc.InstanceName))
                    return;
            }
            Found?.Invoke(new DiscoveredServer(svc.InstanceName, id ?? "", svc.Address, svc.Port));
        };
    }

    public void Start() => _browser.Start();

    public void Dispose() => _browser.Dispose();
}