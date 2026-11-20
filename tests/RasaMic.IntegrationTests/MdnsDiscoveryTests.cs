using System.Net;
using RasaMic.Core.Discovery;
using Xunit;

namespace RasaMic.IntegrationTests;

public class MdnsDiscoveryTests
{
    [Fact]
    public async Task AdvertiseAndBrowse_FindsLocalServer()
    {
        string instance = "rasa-test-" + Guid.NewGuid().ToString("N")[..8];
        const int port = 47399;
        using var advertiser = new RasaMdnsAdvertiser(instance, "test-server-id", port);
        advertiser.Start();

        var found = new TaskCompletionSource<DiscoveredServer>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var browser = new RasaMdnsBrowser();
        browser.Found += s =>
        {
            if (s.Name == instance)
                found.TrySetResult(s);
        };
        browser.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using (cts.Token.Register(() => found.TrySetCanceled()))
        {
            DiscoveredServer server = await found.Task;
            Assert.Equal("test-server-id", server.Id);
            Assert.Equal(port, server.Port);
            Assert.NotNull(server.Address);
            // Loopback answers must be filtered: with adb-reverse active they
            // hairpin a phone back into the PC's own tunnel (seen on device).
            Assert.False(IPAddress.IsLoopback(server.Address));
        }
    }
}