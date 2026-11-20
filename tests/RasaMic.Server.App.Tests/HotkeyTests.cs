using RasaMic.Server.App.Services;
using SharpHook.Data;
using Xunit;

namespace RasaMic.Server.App.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+M")]
    [InlineData("Ctrl+Shift+F9")]
    [InlineData("Alt+X")]
    [InlineData("Ctrl+Alt+Shift+0")]
    [InlineData("ctrl+alt+f12")]
    public void Parse_Format_Roundtrip(string text)
    {
        Assert.True(Hotkey.TryParse(text, out Hotkey hk));
        Assert.True(Hotkey.TryParse(hk.ToString(), out Hotkey again));
        Assert.Equal(hk, again);
    }

    [Fact]
    public void Default_IsCtrlAltM()
    {
        Assert.Equal("Ctrl+Alt+M", Hotkey.Default.ToString());
        Assert.Equal(KeyCode.VcM, Hotkey.Default.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("M")]
    [InlineData("Ctrl+M+X")]
    [InlineData("Ctrl+Win+M")]
    [InlineData("Ctrl+F13")]
    [InlineData("Ctrl+Space")]
    public void Parse_RejectsInvalid(string text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }

    [Fact]
    public void Settings_RoundtripAndDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rasamic-settings-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal("Ctrl+Alt+M", SettingsStore.Load(path).MuteHotkey);

            SettingsStore.Save(new AppSettings("Ctrl+Shift+F9"), path);
            Assert.Equal("Ctrl+Shift+F9", SettingsStore.Load(path).MuteHotkey);

            File.WriteAllText(path, "{corrupt");
            Assert.Equal("Ctrl+Alt+M", SettingsStore.Load(path).MuteHotkey);
        }
        finally
        {
            File.Delete(path);
        }
    }
}