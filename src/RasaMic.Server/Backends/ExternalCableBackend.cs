using RasaMic.Audio;

namespace RasaMic.Server.Backends;

/// <summary>Windows virtual microphone through an externally installed audio cable.</summary>
public sealed class ExternalCableBackend : IVirtualMicBackend, IAudioOutputDiagnostics
{
    public const string DefaultEndpointName = "CABLE Input";

    private readonly string _endpointName;
    private readonly string? _endpointId;
    private MonitorOutput? _output;

    public ExternalCableBackend(string endpointName = DefaultEndpointName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        _endpointName = endpointName;
    }

    public ExternalCableBackend(AudioDevice endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Name);
        _endpointName = endpoint.Name;
        _endpointId = string.IsNullOrWhiteSpace(endpoint.Id) ? null : endpoint.Id;
    }

    public string Name => $"External cable ({_endpointName})";
    public bool StereoCapable => true;
    public double BufferedMs => _output?.GetStats().BufferedMs ?? 0;
    public double DeviceBufferMs => _output?.GetStats().DeviceBufferMs ?? 0;
    public ulong DroppedSamples => _output?.GetStats().DroppedSamples ?? 0;

    public void Validate()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The external-cable backend is Windows-only.");

        IReadOnlyList<AudioDevice> devices = AudioDeviceEnumerator.EnumeratePlayback();
        bool found = _endpointId is null
            ? devices.Any(d => d.Name.Contains(_endpointName, StringComparison.Ordinal))
            : devices.Any(d => d.Id == _endpointId);
        if (!found)
            throw Unavailable();
    }

    public void Start(int sampleRate, int channels)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The external-cable backend is Windows-only.");

        Stop();
        try
        {
            _output = _endpointId is null
                ? new MonitorOutput(sampleRate, channels, _endpointName)
                : new MonitorOutput(sampleRate, channels, new AudioDevice(_endpointId, _endpointName, false));
        }
        catch (InvalidOperationException ex)
        {
            throw Unavailable(ex);
        }
    }

    private InvalidOperationException Unavailable(Exception? inner = null) => new(
        $"Audio cable playback endpoint '{_endpointName}' is unavailable. Install VB-CABLE from https://vb-audio.com/Cable/ " +
        "or choose an available endpoint, then select 'CABLE Output' as the microphone in the target app.", inner);

    public void Write(ReadOnlySpan<float> pcm) => _output?.Write(pcm);

    public void Stop()
    {
        _output?.Dispose();
        _output = null;
    }

    public void Dispose() => Stop();
}
