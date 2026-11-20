using System.Runtime.InteropServices;

namespace RasaMic.Audio;

/// <summary>
/// Microphone capture (Oboe on Android, miniaudio elsewhere).
/// The callback runs on the native RT thread — feed the pipeline directly, no allocations.
/// </summary>
public sealed class CaptureDevice : ICaptureSource
{
    private delegate void PcmCallback(IntPtr user, IntPtr pcm, int samples);

    private static readonly unsafe PcmCallback NativeCallback = OnPcm;
    private readonly Action<ReadOnlySpan<short>> _onPcm;
    private GCHandle _selfHandle;
    private IntPtr _handle;

    public unsafe CaptureDevice(int sampleRate, int channels, Action<ReadOnlySpan<short>> onPcm)
    {
        _onPcm = onPcm;
        _selfHandle = GCHandle.Alloc(this);
        _handle = NativeMethods.rm_capture_create(sampleRate, channels, Marshal.GetFunctionPointerForDelegate(NativeCallback), GCHandle.ToIntPtr(_selfHandle));
        if (_handle == IntPtr.Zero)
        {
            _selfHandle.Free();
            throw new InvalidOperationException("Failed to open microphone capture device.");
        }
    }

    private static unsafe void OnPcm(IntPtr user, IntPtr pcm, int samples)
    {
        var self = (CaptureDevice?)GCHandle.FromIntPtr(user).Target;
        self?._onPcm(new ReadOnlySpan<short>((void*)pcm, samples));
    }

    public void Start()
    {
        if (NativeMethods.rm_capture_start(_handle) != 0)
            throw new InvalidOperationException("Failed to start microphone capture.");
    }

    public void Stop() => NativeMethods.rm_capture_stop(_handle);

    /// <summary>0 omni / 1 subcardioid / 2 cardioid / 3 hypercardioid.
    /// iOS only; returns false where the input has no polar patterns.</summary>
    public bool SetPolarPattern(int pattern) => NativeMethods.rm_capture_set_polar(_handle, pattern) == 0;

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.rm_capture_destroy(_handle);
            _handle = IntPtr.Zero;
            _selfHandle.Free();
        }
    }
}