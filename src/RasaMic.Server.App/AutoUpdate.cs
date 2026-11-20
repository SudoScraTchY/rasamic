using Velopack;

namespace RasaMic.Server.App;

/// <summary>
/// Best-effort auto-update for Velopack-installed builds. Set RASA_UPDATE_FEED
/// to the release feed URL (the directory holding RELEASES + nupkgs, e.g. a
/// GitHub release asset mirror). Downloads in the background and applies on
/// next exit — never restarts mid-stream.
/// </summary>
internal static class AutoUpdate
{
    public static async Task CheckAsync()
    {
        string? feed = Environment.GetEnvironmentVariable("RASA_UPDATE_FEED");
        if (string.IsNullOrEmpty(feed))
            return;
        try
        {
            var mgr = new UpdateManager(feed);
            UpdateInfo? info = await mgr.CheckForUpdatesAsync();
            if (info is null)
                return;
            await mgr.DownloadUpdatesAsync(info);
            mgr.WaitExitThenApplyUpdates(info);
        }
        catch { /* not installed, feed unreachable — updates are optional */ }
    }
}