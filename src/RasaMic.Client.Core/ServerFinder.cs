using System.Net;
using System.Net.Sockets;
using RasaMic.Core.Discovery;
using RasaMic.Protocol;

namespace RasaMic.Client.Core;

/// <summary>
/// Smart-reconnect ladder (plan): last-known IP (0.5 s) → mDNS (2 s) → beacon (2 s)
/// → parallel /24 TCP probe (≤64 concurrent, 300 ms). Identity is verified by the
/// pinned-key handshake afterwards, so a probe hit is only a candidate.
/// </summary>
public static class ServerFinder
{
    public static async Task<IPEndPoint?> FindAsync(
        KnownServer known, CancellationToken ct,
        TimeSpan? mdnsTimeout = null, TimeSpan? beaconTimeout = null, string? probeSubnet = null)
    {
        foreach (string ip in known.LastIps)
        {
            if (IPAddress.TryParse(ip, out IPAddress? addr) &&
                await TcpProbeAsync(addr, known.Port, TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false))
                return new IPEndPoint(addr, known.Port);
        }

        TimeSpan mdnsFor = mdnsTimeout ?? TimeSpan.FromSeconds(2);
        var found = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var browser = new RasaMdnsBrowser())
        {
            browser.Found += s =>
            {
                if (s.Id == known.ServerId)
                    found.TrySetResult(new IPEndPoint(s.Address, s.Port));
            };
            browser.Start();
            IPEndPoint? hit = await WaitAsync(found.Task, mdnsFor, ct).ConfigureAwait(false);
            if (hit is not null)
                return hit;
        }

        TimeSpan beaconFor = beaconTimeout ?? TimeSpan.FromSeconds(2);
        try
        {
            using var listener = new BeaconListener();
            using var beaconCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            beaconCts.CancelAfter(beaconFor);
            while (!beaconCts.IsCancellationRequested)
            {
                (ServerBeacon Beacon, IPEndPoint Source)? hit =
                    await listener.ReceiveFromAsync(beaconCts.Token).ConfigureAwait(false);
                if (hit is null)
                    break;
                if (hit.Value.Beacon.Id == known.ServerId)
                    return new IPEndPoint(hit.Value.Source.Address, hit.Value.Beacon.Tcp);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (SocketException) { /* beacon port unavailable */ }

        string subnet = probeSubnet ?? GuessSubnet(known.LastIps);
        if (subnet.Length > 0)
        {
            IPEndPoint? probed = await ProbeSubnetAsync(subnet, known.Port, ct).ConfigureAwait(false);
            if (probed is not null)
                return probed;
        }
        return null;
    }

    /// <summary>Retries the ladder with jittered 0.5→8 s backoff until connected or cancelled.</summary>
    public static async Task<ClientSession?> ConnectWithRetryAsync(KnownServer known, string clientName, CancellationToken ct, long features = 0)
    {
        var pinned = new PairingPayload(ProtocolConstants.Version, known.ServerId, known.Name,
            Array.Empty<string>(), known.Port, known.StaticPubBase64Url);
        double delay = 0.5;
        while (!ct.IsCancellationRequested)
        {
            IPEndPoint? ep;
            try
            {
                ep = await FindAsync(known, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            if (ep is not null)
            {
                var session = new ClientSession();
                try
                {
                    await session.ConnectAsync(ep, pinned, clientName, ct, known.TcpAudio, features: features).ConfigureAwait(false);
                    return session;
                }
                catch (OperationCanceledException) { session.Dispose(); throw; }
                catch (Exception) { session.Dispose(); }
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay + Random.Shared.NextDouble() * 0.25), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            delay = Math.Min(delay * 2, 8);
        }
        return null;
    }

    private static string GuessSubnet(string[] lastIps)
    {
        foreach (string ip in lastIps)
        {
            int lastDot = ip.LastIndexOf('.');
            if (lastDot > 0 && IPAddress.TryParse(ip, out IPAddress? a) && a.AddressFamily == AddressFamily.InterNetwork)
                return ip[..lastDot];
        }
        return "";
    }

    private static async Task<IPEndPoint?> ProbeSubnetAsync(string subnet, int port, CancellationToken ct)
    {
        // adb-reverse listens on the phone's own interfaces; probing ourselves
        // hairpins back into the PC tunnel and poisons the remembered endpoint.
        var own = new HashSet<IPAddress>();
        foreach (System.Net.NetworkInformation.NetworkInterface ni in
            System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            foreach (System.Net.NetworkInformation.UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                own.Add(ua.Address);
        using var gate = new SemaphoreSlim(64);
        var done = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new List<Task>();
        for (int host = 1; host <= 254 && !done.Task.IsCompleted; host++)
        {
            if (!IPAddress.TryParse($"{subnet}.{host}", out IPAddress? addr) || own.Contains(addr))
                continue;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (await TcpProbeAsync(addr, port, TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false))
                        done.TrySetResult(new IPEndPoint(addr, port));
                }
                finally
                {
                    gate.Release();
                }
            }, CancellationToken.None));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return done.Task.IsCompletedSuccessfully ? done.Task.Result : null;
    }

    private static async Task<bool> TcpProbeAsync(IPAddress addr, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient(addr.AddressFamily);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            await tcp.ConnectAsync(addr, port, timeoutCts.Token).ConfigureAwait(false);
            return tcp.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<IPEndPoint?> WaitAsync(Task<IPEndPoint> task, TimeSpan timeout, CancellationToken ct)
    {
        var delay = Task.Delay(timeout, ct);
        Task finished = await Task.WhenAny(task, delay).ConfigureAwait(false);
        return finished == task && task.IsCompletedSuccessfully ? task.Result : null;
    }
}