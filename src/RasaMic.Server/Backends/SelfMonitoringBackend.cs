namespace RasaMic.Server.Backends;

/// <summary>Feeds a virtual-microphone backend while also playing phone audio on the PC.</summary>
public sealed class SelfMonitoringBackend : IVirtualMicBackend, IAudioOutputDiagnostics
{
    private readonly IVirtualMicBackend _primary;
    private readonly Func<IVirtualMicBackend> _monitorFactory;
    private IVirtualMicBackend? _monitor;

    public SelfMonitoringBackend(IVirtualMicBackend primary, Func<IVirtualMicBackend>? monitorFactory = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        _primary = primary;
        _monitorFactory = monitorFactory ?? (() => new MonitorBackend());
    }

    public string Name => $"{_primary.Name} + PC speakers";
    public bool StereoCapable => _primary.StereoCapable;
    public double BufferedMs => Math.Max(Buffered(_primary), Buffered(_monitor));
    public double DeviceBufferMs => Math.Max(DeviceBuffered(_primary), DeviceBuffered(_monitor));
    public ulong DroppedSamples => Dropped(_primary) + Dropped(_monitor);

    private static double Buffered(IVirtualMicBackend? backend) =>
        backend is IAudioOutputDiagnostics diagnostics ? diagnostics.BufferedMs : 0;

    private static double DeviceBuffered(IVirtualMicBackend? backend) =>
        backend is IAudioOutputDiagnostics diagnostics ? diagnostics.DeviceBufferMs : 0;

    private static ulong Dropped(IVirtualMicBackend? backend) =>
        backend is IAudioOutputDiagnostics diagnostics ? diagnostics.DroppedSamples : 0;

    public void Start(int sampleRate, int channels)
    {
        if (_monitor is not null)
            Stop();
        try
        {
            _primary.Start(sampleRate, channels);
            _monitor = _monitorFactory();
            _monitor.Start(sampleRate, channels);
        }
        catch
        {
            StopSafely(_monitor);
            DisposeSafely(_monitor);
            _monitor = null;
            StopSafely(_primary);
            throw;
        }
    }

    public void Write(ReadOnlySpan<float> pcm)
    {
        _primary.Write(pcm);
        _monitor?.Write(pcm);
    }

    public void Stop()
    {
        IVirtualMicBackend? monitor = _monitor;
        try
        {
            monitor?.Stop();
        }
        finally
        {
            try
            {
                _primary.Stop();
            }
            finally
            {
                DisposeSafely(monitor);
                _monitor = null;
            }
        }
    }

    public void Dispose()
    {
        IVirtualMicBackend? monitor = _monitor;
        _monitor = null;
        try
        {
            monitor?.Dispose();
        }
        finally
        {
            _primary.Dispose();
        }
    }

    private static void StopSafely(IVirtualMicBackend? backend)
    {
        try
        {
            backend?.Stop();
        }
        catch
        {
            // Preserve the original Start failure after attempting both cleanups.
        }
    }

    private static void DisposeSafely(IVirtualMicBackend? backend)
    {
        try
        {
            backend?.Dispose();
        }
        catch
        {
            // Preserve the original start/stop failure.
        }
    }
}
