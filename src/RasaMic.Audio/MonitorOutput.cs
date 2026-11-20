namespace RasaMic.Audio;

/// <summary>Server-side speaker playback (miniaudio) for driverless monitor mode.</summary>
public sealed class MonitorOutput : IDisposable
{
    private IntPtr _handle;

    public MonitorOutput(int sampleRate = 48000, int channels = 1)
    {
        _handle = NativeMethods.rm_monitor_create(sampleRate, channels);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to open monitor output device.");
    }

    /// <summary>Opens the first playback device whose name contains <paramref name="nameFilter"/>.</summary>
    public MonitorOutput(int sampleRate, int channels, string nameFilter)
    {
        _handle = NativeMethods.rm_monitor_create_named(sampleRate, channels, nameFilter);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException($"No playback device matching '{nameFilter}'.");
    }

    public unsafe int Write(ReadOnlySpan<float> pcm)
    {
        fixed (float* p = pcm)
            return NativeMethods.rm_monitor_write(_handle, p, pcm.Length);
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