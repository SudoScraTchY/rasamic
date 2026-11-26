using System.Diagnostics;
using Microsoft.Win32;

namespace RasaMic.Server;

/// <summary>
/// Login auto-start: HKCU Run key (Windows), LaunchAgent (macOS),
/// systemd user unit (Linux). macOS/Linux write the file only; the user
/// enables it with launchctl / systemctl (printed by the CLI).
/// </summary>
public static class AutoStart
{
    public const string ValueName = "RasaMic";
    public const string LaunchAgentLabel = "dev.rasamic.server";

    public static string LaunchAgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", $"{LaunchAgentLabel}.plist");

    public static string SystemdUnitPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "systemd", "user", "rasamicd.service");

    public static string LaunchAgentPlist(string exePath, string args) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{LaunchAgentLabel}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{exePath}</string>
                <string>{args}</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>KeepAlive</key>
            <true/>
        </dict>
        </plist>
        """;

    public static string SystemdUnit(string exePath, string args) => $"""
        [Unit]
        Description=Rasa microphone server
        After=network-online.target sound.target

        [Service]
        ExecStart={exePath} {args}
        Restart=on-failure
        RestartSec=3

        [Install]
        WantedBy=default.target
        """;

    public static void Enable(string exePath, string args = "--monitor")
    {
        if (OperatingSystem.IsWindows())
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key.SetValue(ValueName, $"\"{exePath}\" {args}");
        }
        else if (OperatingSystem.IsMacOS())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LaunchAgentPath)!);
            File.WriteAllText(LaunchAgentPath, LaunchAgentPlist(exePath, args));
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SystemdUnitPath)!);
            File.WriteAllText(SystemdUnitPath, SystemdUnit(exePath, args));
        }
    }

    public static void Disable()
    {
        if (OperatingSystem.IsWindows())
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else if (OperatingSystem.IsMacOS())
        {
            File.Delete(LaunchAgentPath);
        }
        else
        {
            File.Delete(SystemdUnitPath);
        }
    }

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue(ValueName) is not null;
        }
        return OperatingSystem.IsMacOS() ? File.Exists(LaunchAgentPath) : File.Exists(SystemdUnitPath);
    }

    public static string CurrentExePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "rasamicd";
}