namespace RasaMic.Server;

/// <summary>
/// A virtual-microphone sink. Implementations: monitor (speakers), Windows ACX driver,
/// macOS HAL plugin, Linux PipeWire. Write is called from the decode thread with
/// decoded float PCM at the session sample rate.
/// </summary>
public interface IVirtualMicBackend : IDisposable
{
    string Name { get; }
    /// <summary>False = mono-only sink; the server downmixes stereo sessions before Write.</summary>
    bool StereoCapable => false;
    void Start(int sampleRate, int channels);
    void Write(ReadOnlySpan<float> pcm);
    void Stop();
}