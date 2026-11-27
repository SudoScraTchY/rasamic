using System.Diagnostics;
using RasaMic.Audio;
using RasaMic.Server;

namespace RasaMic.Server.Cli;

public static class Doctor
{
    public static void Run()
    {
        Console.WriteLine("Rasa server doctor");
        if (OperatingSystem.IsLinux())
        {
            bool pw = ToolExists("pw-loopback");
            bool pulse = ToolExists("pactl");
            Console.WriteLine($"pw-loopback: {(pw ? "found" : "MISSING")}");
            Console.WriteLine($"pactl:       {(pulse ? "found" : "MISSING")}");
            string? runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            bool pulseSocket = runtimeDir is not null && File.Exists(Path.Combine(runtimeDir, "pulse", "native"));
            Console.WriteLine($"Pulse socket: {(pulseSocket ? "found" : "MISSING (start pipewire-pulse)")}");
            if (!pw && !pulse)
            {
                Console.WriteLine("Install one of:");
                Console.WriteLine("  Fedora:  sudo dnf install pipewire-utils pipewire-pulse");
                Console.WriteLine("  Debian:  sudo apt install pipewire-audio pipewire-bin pipewire-pulse");
                Console.WriteLine("  Arch:    sudo pacman -S pipewire pipewire-pulse");
            }
            else
            {
                Console.WriteLine("Start the virtual mic with: rasamicd --pipewire");
                Console.WriteLine("Verify the source appears:  pw-cli ls Node | grep rasamic-mic");
                if (!pulseSocket)
                    Console.WriteLine("Audio feed needs the Pulse compat daemon: pipewire-pulse &");
            }
            Console.WriteLine($"Auto-start: rasamicd --autostart enable (systemd user unit at {AutoStart.SystemdUnitPath})");
        }
        else if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("Driverless demo works now: rasamicd --monitor");
            Console.WriteLine("External cable (recommended): install VB-CABLE from https://vb-audio.com/Cable/");
            try
            {
                var playback = AudioDeviceEnumerator.EnumeratePlayback();
                var capture = AudioDeviceEnumerator.EnumerateCapture();
                bool cableInput = playback.Any(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
                bool cableOutput = capture.Any(d => d.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"VB-CABLE input:  {(cableInput ? "found" : "MISSING")}");
                Console.WriteLine($"VB-CABLE output: {(cableOutput ? "found" : "MISSING")}");
                if (cableInput && cableOutput)
                    Console.WriteLine("Start with: rasamicd --cable; select CABLE Output as the microphone in Discord/OBS.");
                else
                    Console.WriteLine("Install VB-CABLE, then run: rasamicd --list-outputs");
            }
            catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                Console.WriteLine($"Audio endpoint check failed: {ex.Message}");
            }
            Console.WriteLine("Project driver: see docs/windows-driver-feed.md (test-signed install).");
            Console.WriteLine($"Auto-start: rasamicd --autostart enable (HKCU Run key)");
        }
        else if (OperatingSystem.IsMacOS())
        {
            Console.WriteLine("Driverless demo works now: rasamicd --monitor");
            Console.WriteLine("HAL virtual mic plugin: planned (M5).");
            Console.WriteLine($"Auto-start: rasamicd --autostart enable (LaunchAgent at {AutoStart.LaunchAgentPath})");
        }
    }

    private static bool ToolExists(string tool)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null)
                return false;
            p.WaitForExit(3000);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}