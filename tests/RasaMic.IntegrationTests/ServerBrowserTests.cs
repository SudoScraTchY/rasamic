using System.Net;
using System.Net.Sockets;
using RasaMic.Client.Core;
using RasaMic.Core.Discovery;
using Xunit;

namespace RasaMic.IntegrationTests;

public class ServerBrowserTests
{
    [Fact]
    public async Task Beacon_found_once_per_endpoint()
    {
        const int port = 47398;
        using var browser = new ServerBrowser(mdns: false, beaconPort: port);
        var events = new List<FoundServer>();
        browser.Found += s => { lock (events) events.Add(s); };
        browser.Start();

        using var broadcaster = new BeaconBroadcaster(
            new ServerBeacon(1, "browse-id", "browse-name", 47300),
            port, new IPEndPoint(IPAddress.Loopback, port), TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource(3000);
        _ = Swallow(broadcaster.RunAsync(cts.Token));

        await WaitUntil(() => { lock (events) return events.Count > 0; }, 2000);
        lock (events)
        {
            FoundServer s = Assert.Single(events);
            Assert.Equal("browse-id", s.Id);
            Assert.Equal("browse-name", s.Name);
            Assert.Equal(47300, s.Port);
            Assert.Equal(IPAddress.Loopback, s.Address);
        }
        await Task.Delay(300);
        lock (events) Assert.Single(events);
        Assert.Single(browser.Servers);
    }

    [Fact]
    public async Task Endpoint_change_refires()
    {
        const int port = 47397;
        using var browser = new ServerBrowser(mdns: false, beaconPort: port);
        var events = new List<FoundServer>();
        browser.Found += s => { lock (events) events.Add(s); };
        browser.Start();

        using (var b1 = new BeaconBroadcaster(new ServerBeacon(1, "move-id", "m", 47300),
                   port, new IPEndPoint(IPAddress.Loopback, port), TimeSpan.FromMilliseconds(50)))
        using (var c1 = new CancellationTokenSource(1500))
        {
            _ = Swallow(b1.RunAsync(c1.Token));
            await WaitUntil(() => { lock (events) return events.Count == 1; }, 2000);
        }

        using var b2 = new BeaconBroadcaster(new ServerBeacon(1, "move-id", "m", 47301),
            port, new IPEndPoint(IPAddress.Loopback, port), TimeSpan.FromMilliseconds(50));
        using var c2 = new CancellationTokenSource(2000);
        _ = Swallow(b2.RunAsync(c2.Token));
        await WaitUntil(() => { lock (events) return events.Count == 2; }, 2000);
        lock (events) Assert.Equal(47301, events[1].Port);
    }

    private static async Task Swallow(Task t)
    {
        try { await t; }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task WaitUntil(Func<bool> cond, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms)
            await Task.Delay(25);
        Assert.True(cond(), "condition not met in time");
    }
}