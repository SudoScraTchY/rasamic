using System.Diagnostics;
using RasaMic.Audio;

namespace RasaMic.Server.Backends;

/// <summary>
/// Linux virtual microphone: pw-loopback exposes a "Rasa Mic" source; received
/// audio is played into its sink via miniaudio. Falls back to PulseAudio
/// null-sink + remap-source when pw-loopback is unavailable.
/// </summary>
public sealed class PipeWireBackend : IVirtualMicBackend
{
    private const string SourceName = "rasamic-mic";
    private const string SinkName = "rasamic-mic-sink";
    // miniaudio matches devices by Pulse description, not sink name.
    private const string SinkDescription = "RasaMic-ServerFeed";

    private MonitorOutput? _output;
    private Process? _loopback;
    private readonly List<int> _pulseModules = new();

    public string Name => "PipeWire/PulseAudio virtual mic";
    public bool StereoCapable => true;

    public void Start(int sampleRate, int channels)
    {
        Stop();
        // Pulse null-sink + remap-source first: pipewire-pulse and PulseAudio both
        // wire the remap internally. pw-loopback's capture link needs a session
        // manager and stays suspended on headless/minimal setups.
        if (TryStartPulse(sampleRate, channels))
            return;
        string map = channels == 2 ? "[FL FR]" : "[MONO]";
        if (TryStartPwLoopback(map, sampleRate, channels))
            return;
        throw new InvalidOperationException(
            "Neither pactl nor pw-loopback is available. Install PipeWire (pipewire-utils + pipewire-pulse) or PulseAudio; run 'rasamicd doctor'.");
    }

    private bool TryStartPwLoopback(string map, int sampleRate, int channels)
    {
        var psi = new ProcessStartInfo("pw-loopback")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add(map);
        psi.ArgumentList.Add($"--capture-props=media.class=Audio/Source node.name={SourceName} node.description=\"Rasa Mic\"");
        psi.ArgumentList.Add($"--playback-props=node.name={SinkName} node.description={SinkDescription}");
        try
        {
            _loopback = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        if (_loopback is null)
            return false;
        _loopback.OutputDataReceived += (_, _) => { };
        _loopback.ErrorDataReceived += (_, _) => { };
        _loopback.BeginOutputReadLine();
        _loopback.BeginErrorReadLine();

        // The sink node takes a moment to register with the PipeWire graph.
        for (int i = 0; i < 50; i++)
        {
            if (_loopback.HasExited)
                break;
            try
            {
                _output = new MonitorOutput(sampleRate, channels, SinkDescription);
                return true;
            }
            catch (InvalidOperationException)
            {
                Thread.Sleep(100);
            }
        }
        Stop();
        return false;
    }

    private bool TryStartPulse(int sampleRate, int channels)
    {
        int? sink = RunPactl($"load-module module-null-sink sink_name={SinkName} sink_properties=device.description={SinkDescription}");
        if (sink is null)
            return false;
        _pulseModules.Add(sink.Value);
        int? source = RunPactl($"load-module module-remap-source master={SinkName}.monitor source_name={SourceName} source_properties=device.description=RasaMic");
        if (source is null)
        {
            Stop();
            return false;
        }
        _pulseModules.Add(source.Value);
        try
        {
            _output = new MonitorOutput(sampleRate, channels, SinkDescription);
            return true;
        }
        catch (InvalidOperationException)
        {
            Stop();
            return false;
        }
    }

    private static int? RunPactl(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pactl", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null)
                return null;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return p.ExitCode == 0 && int.TryParse(output, out int id) ? id : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public void Write(ReadOnlySpan<float> pcm) => _output?.Write(pcm);

    public void Stop()
    {
        _output?.Dispose();
        _output = null;
        if (_loopback is not null)
        {
            try
            {
                if (!_loopback.HasExited)
                    _loopback.Kill(entireProcessTree: true);
                _loopback.WaitForExit(2000);
            }
            catch (InvalidOperationException) { }
            _loopback.Dispose();
            _loopback = null;
        }
        foreach (int id in _pulseModules)
            RunPactl($"unload-module {id}");
        _pulseModules.Clear();
    }

    public void Dispose() => Stop();
}