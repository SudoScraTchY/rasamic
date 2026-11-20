using RasaMic.Server;
using Xunit;

namespace RasaMic.IntegrationTests;

public class AutoStartTests
{
    [Fact]
    public void LaunchAgentPlist_ContainsRequiredKeys()
    {
        string plist = AutoStart.LaunchAgentPlist("/usr/local/bin/rasamicd", "--monitor");
        Assert.Contains("<key>Label</key>", plist);
        Assert.Contains(AutoStart.LaunchAgentLabel, plist);
        Assert.Contains("<string>/usr/local/bin/rasamicd</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);
        Assert.Contains("<key>KeepAlive</key>", plist);
    }

    [Fact]
    public void SystemdUnit_ContainsExecStartAndInstall()
    {
        string unit = AutoStart.SystemdUnit("/usr/bin/rasamicd", "--monitor");
        Assert.Contains("ExecStart=/usr/bin/rasamicd --monitor", unit);
        Assert.Contains("Restart=on-failure", unit);
        Assert.Contains("WantedBy=default.target", unit);
    }

    [Fact]
    public void EnableDisable_Roundtrip_CurrentPlatform()
    {
        // On Windows this touches the real HKCU Run key; always cleaned up.
        bool wasEnabled = AutoStart.IsEnabled();
        try
        {
            AutoStart.Enable(AutoStart.CurrentExePath);
            Assert.True(AutoStart.IsEnabled());
            AutoStart.Disable();
            Assert.False(AutoStart.IsEnabled());
        }
        finally
        {
            if (wasEnabled)
                AutoStart.Enable(AutoStart.CurrentExePath);
            else
                AutoStart.Disable();
        }
    }
}