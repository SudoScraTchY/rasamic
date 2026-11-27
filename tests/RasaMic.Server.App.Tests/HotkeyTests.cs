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
            var defaults = SettingsStore.Load(path);
            Assert.Equal("Ctrl+Alt+M", defaults.MuteHotkey);
            Assert.True(defaults.AudioEncrypted);
            Assert.True(defaults.HearOnPc);

            SettingsStore.Save(new AppSettings("Ctrl+Shift+F9"), path);
            string defaultJson = File.ReadAllText(path);
            Assert.Contains("\"AudioEncrypted\":true", defaultJson);
            Assert.Contains("\"HearOnPc\":true", defaultJson);
            Assert.Equal("Ctrl+Shift+F9", SettingsStore.Load(path).MuteHotkey);

            SettingsStore.Save(new AppSettings(
                "Ctrl+Shift+F9", 2, "{endpoint-id}", "CABLE Input", AudioEncrypted: false, HearOnPc: false), path);
            var saved = SettingsStore.Load(path);
            Assert.Equal(2, saved.BackendChoice);
            Assert.Equal("{endpoint-id}", saved.OutputDeviceId);
            Assert.Equal("CABLE Input", saved.OutputDeviceName);
            Assert.False(saved.AudioEncrypted);
            Assert.False(saved.HearOnPc);

            File.WriteAllText(path, "{\"MuteHotkey\":\"Ctrl+Alt+M\"}");
            var legacy = SettingsStore.Load(path);
            Assert.Equal("Ctrl+Alt+M", legacy.MuteHotkey);
            Assert.Equal(0, legacy.BackendChoice);
            Assert.Null(legacy.OutputDeviceId);
            Assert.True(legacy.AudioEncrypted);
            Assert.True(legacy.HearOnPc);

            File.WriteAllText(path, "{corrupt");
            var corrupt = SettingsStore.Load(path);
            Assert.Equal("Ctrl+Alt+M", corrupt.MuteHotkey);
            Assert.True(corrupt.AudioEncrypted);
            Assert.True(corrupt.HearOnPc);
        }
        finally
        {
            File.Delete(path);
        }
    }
}