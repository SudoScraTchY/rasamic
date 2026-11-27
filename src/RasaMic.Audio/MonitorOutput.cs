namespace RasaMic.Audio;

public sealed record MonitorStats(
    int CapacitySamples, int QueuedSamples, ulong DroppedSamples,
    ulong UnderrunSamples, double BufferedMs,
    double DeviceBufferMs, double DevicePeriodMs);

/// <summary>Server-side speaker playback (miniaudio) for driverless monitor mode.</summary>
public sealed class MonitorOutput : IDisposable
{
    private readonly int _sampleRate;
    private readonly int _channels;
    private IntPtr _handle;

    public MonitorOutput(int sampleRate = 48000, int channels = 1)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _handle = NativeMethods.rm_monitor_create(sampleRate, channels);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to open monitor output device.");
    }

    /// <summary>Opens the first playback device whose name contains <paramref name="nameFilter"/>.</summary>
    public MonitorOutput(int sampleRate, int channels, string nameFilter)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _handle = NativeMethods.rm_monitor_create_named(sampleRate, channels, nameFilter);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException($"No playback device matching '{nameFilter}'.");
    }

    /// <summary>Opens a playback device by stable endpoint ID, or by exact name when no ID is available.</summary>
    public MonitorOutput(int sampleRate, int channels, AudioDevice device)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Name);

        if (!string.IsNullOrWhiteSpace(device.Id))
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Stable audio endpoint IDs are supported on Windows only.");
            _handle = NativeMethods.rm_monitor_create_id(sampleRate, channels, device.Id);
        }
        else
        {
            _handle = NativeMethods.rm_monitor_create_named(sampleRate, channels, device.Name);
        }

        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException($"Playback device '{device.Name}' is unavailable.");
    }

    public unsafe int Write(ReadOnlySpan<float> pcm)
    {
        fixed (float* p = pcm)
            return NativeMethods.rm_monitor_write(_handle, p, pcm.Length);
    }

    public MonitorStats GetStats()
    {
        NativeMethods.rm_monitor_get_stats(_handle, out RmMonitorStats stats);
        double bufferedMs = _sampleRate > 0 && _channels > 0
            ? stats.QueuedSamples * 1000.0 / (_sampleRate * _channels)
            : 0;
        double deviceBufferMs = stats.DeviceSampleRate > 0
            ? stats.DeviceBufferFrames * 1000.0 / stats.DeviceSampleRate : 0;
        double devicePeriodMs = stats.DeviceSampleRate > 0
            ? stats.DevicePeriodFrames * 1000.0 / stats.DeviceSampleRate : 0;
        return new MonitorStats(
            (int)stats.CapacitySamples, (int)stats.QueuedSamples, stats.DroppedSamples,
            stats.UnderrunSamples, bufferedMs, deviceBufferMs, devicePeriodMs);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.rm_monitor_destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }
}