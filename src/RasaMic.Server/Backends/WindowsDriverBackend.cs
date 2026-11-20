using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace RasaMic.Server.Backends;

/// <summary>
/// Feeds the Rasa Mic Windows driver (drivers/windows) through its control
/// device interface. The kernel endpoint is 48 kHz stereo int32; conversion
/// from the decoded mono float stream happens here so the kernel stays dumb.
/// Requires the test-signed driver to be installed (see docs/windows-driver-feed.md).
/// </summary>
public sealed class WindowsDriverBackend : IVirtualMicBackend
{
    private static readonly Guid CtlInterfaceGuid = new("8B4E9D2A-3C61-4F7E-9A0D-2E5B6C8F1A47");

    // CTL_CODE(0x8000, 0x800/0x801, METHOD_IN_DIRECT/METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint IoctlWritePcm = (0x8000u << 16) | (0x800u << 2) | 1u;
    private const uint IoctlFlush = (0x8000u << 16) | (0x801u << 2) | 0u;

    private IntPtr _handle = InvalidHandle;
    private byte[] _feed = [];
    private readonly byte[] _result = new byte[12];
    private long _droppedBytes;
    private DriftSlipPolicy? _drift;
    private long _fillFrames;
    private readonly byte[] _lastFrame = new byte[8];
    private bool _hasLastFrame;

    private static IntPtr InvalidHandle => new(-1);

    public string Name => "Rasa Mic (driver)";
    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);
    public long SamplesDropped => _drift?.SamplesDropped ?? 0;
    public long SamplesRepeated => _drift?.SamplesRepeated ?? 0;

    public void Start(int sampleRate, int channels)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Rasa Mic driver backend is Windows-only.");
        if (sampleRate != 48000 || channels != 1)
            throw new ArgumentException("Driver feed expects decoded 48 kHz mono PCM.");

        Stop();
        string path = FindControlDevicePath()
            ?? throw new InvalidOperationException(
                "Rasa Mic control device not found. Install the driver first (pnputil /add-driver rasamic.inf /install).");

        _handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (_handle == InvalidHandle)
            throw new InvalidOperationException($"Failed to open Rasa Mic control device (Win32 error {Marshal.GetLastWin32Error()}).");
        // Kernel ring: 256 KiB of stereo int32 = 32768 frames (rasamic_feed.h).
        _drift = new DriftSlipPolicy(sampleRate, 256 * 1024 / 8);
        _fillFrames = 0;
        _hasLastFrame = false;
    }

    public void Write(ReadOnlySpan<float> pcm)
    {
        if (_handle == InvalidHandle || pcm.Length == 0) return;

        Slip slip = _drift?.Decide(_fillFrames) ?? Slip.None;
        if (slip == Slip.Drop)
            pcm = pcm[1..]; // skip one frame: the kernel reader never sees it
        int repeat = slip == Slip.Repeat && _hasLastFrame ? 1 : 0;
        int frames = pcm.Length + repeat;
        if (frames == 0) return;

        int needed = frames * 2 * sizeof(int);
        if (_feed.Length < needed) _feed = new byte[needed];

        int offset = 0;
        if (repeat == 1)
        {
            _lastFrame.CopyTo(_feed, 0);
            offset = 8;
        }
        for (int i = 0; i < pcm.Length; i++)
        {
            int s = (int)(Math.Clamp(pcm[i], -1f, 1f) * int.MaxValue);
            BinaryPrimitives.WriteInt32LittleEndian(_feed.AsSpan(offset + 8 * i), s);
            BinaryPrimitives.WriteInt32LittleEndian(_feed.AsSpan(offset + 8 * i + 4), s);
        }
        _feed.AsSpan(needed - 8, 8).CopyTo(_lastFrame);
        _hasLastFrame = true;

        bool ok = DeviceIoControl(_handle, IoctlWritePcm,
            _feed, (uint)needed, _result, (uint)_result.Length, out _, IntPtr.Zero);
        if (!ok)
            throw new InvalidOperationException($"IOCTL_RASA_WRITE_PCM failed (Win32 error {Marshal.GetLastWin32Error()}).");
        uint dropped = BinaryPrimitives.ReadUInt32LittleEndian(_result.AsSpan(4));
        if (dropped > 0)
            Interlocked.Add(ref _droppedBytes, dropped);
        _fillFrames = BinaryPrimitives.ReadUInt32LittleEndian(_result.AsSpan(8)) / 8;
    }

    public void Stop()
    {
        if (_handle != InvalidHandle)
        {
            DeviceIoControl(_handle, IoctlFlush, null, 0, null, 0, out _, IntPtr.Zero);
            CloseHandle(_handle);
            _handle = InvalidHandle;
        }
    }

    public void Dispose() => Stop();

    private static string? FindControlDevicePath()
    {
        Guid guid = CtlInterfaceGuid;
        IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == InvalidHandle) return null;

        try
        {
            var iface = new DeviceInterfaceData { CbSize = Marshal.SizeOf<DeviceInterfaceData>() };
            for (uint index = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref iface); index++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required == 0) continue;

                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize is 8 on x64, 6 on x86
                    // (fixed struct prefix, not the full buffer size).
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, required, out _, IntPtr.Zero))
                        continue;
                    return Marshal.PtrToStringUni(detail + 4);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
            return null;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid interfaceClassGuid, uint memberIndex, ref DeviceInterfaceData data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint detailSize, out uint requiredSize, IntPtr deviceInfo);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(IntPtr device, uint ioctlCode, byte[]? inBuffer, uint inBufferSize, byte[]? outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}