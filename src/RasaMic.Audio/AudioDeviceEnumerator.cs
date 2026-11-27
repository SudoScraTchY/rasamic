using System.Runtime.InteropServices;

namespace RasaMic.Audio;

public static class AudioDeviceEnumerator
{
    private const int Playback = 0;
    private const int Capture = 1;

    public static IReadOnlyList<AudioDevice> EnumeratePlayback() => Enumerate(Playback);

    public static IReadOnlyList<AudioDevice> EnumerateCapture() => Enumerate(Capture);

    private static unsafe IReadOnlyList<AudioDevice> Enumerate(int deviceType)
    {
        var collector = new DeviceCollector();
        GCHandle handle = GCHandle.Alloc(collector);
        try
        {
            delegate* unmanaged<IntPtr, IntPtr, IntPtr, int, void> callback = &OnDevice;
            int result = NativeMethods.rm_devices_enumerate(deviceType, callback, GCHandle.ToIntPtr(handle));
            if (result != 0)
                throw new InvalidOperationException($"Native audio device enumeration failed with code {result}.");
            if (collector.Error is not null)
                throw new InvalidOperationException("Failed to enumerate audio devices.", collector.Error);
            return collector.Devices;
        }
        finally
        {
            handle.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static void OnDevice(IntPtr user, IntPtr id, IntPtr name, int isDefault)
    {
        try
        {
            var collector = (DeviceCollector)GCHandle.FromIntPtr(user).Target!;
            collector.Devices.Add(new AudioDevice(
                id == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(id) ?? "",
                name == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(name) ?? "",
                isDefault != 0));
        }
        catch (Exception ex)
        {
            try
            {
                ((DeviceCollector)GCHandle.FromIntPtr(user).Target!).Error = ex;
            }
            catch
            {
                // Native callbacks must never let managed exceptions cross the ABI boundary.
            }
        }
    }

    private sealed class DeviceCollector
    {
        public List<AudioDevice> Devices { get; } = new();
        public Exception? Error { get; set; }
    }
}
