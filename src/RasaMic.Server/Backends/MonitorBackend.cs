using RasaMic.Audio;

namespace RasaMic.Server.Backends;

/// <summary>Driverless demo backend: plays received audio on the default speakers.</summary>
public sealed class MonitorBackend : IVirtualMicBackend
{
    private MonitorOutput? _output;

    public string Name => "Monitor (speakers)";
    public bool StereoCapable => true;

    public void Start(int sampleRate, int channels)
    {
        _output?.Dispose();
        _output = new MonitorOutput(sampleRate, channels);
    }

    public void Write(ReadOnlySpan<float> pcm) => _output?.Write(pcm);

    public void Stop()
    {
        _output?.Dispose();
        _output = null;
    }

    public void Dispose() => Stop();
}