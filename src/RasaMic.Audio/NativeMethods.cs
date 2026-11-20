using System.Runtime.InteropServices;

namespace RasaMic.Audio;

[StructLayout(LayoutKind.Sequential)]
internal struct RmConfig
{
    public int SampleRate;
    public int FrameMs;
    public int Bitrate;
    public int NsEnabled;
    public int AgcEnabled;
    public float GainDb;
    public int Muted;
    public int FecEnabled;
    public int DtxEnabled;
    public int Channels;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RmStats
{
    public ulong FramesProcessed;
    public ulong PacketsProduced;
    public ulong PacketsDropped;
}

internal static partial class NativeMethods
{
    private const string Lib = "rasamic_native";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr rm_pipeline_create(in RmConfig cfg);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rm_pipeline_destroy(IntPtr h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int rm_pipeline_feed(IntPtr h, short* pcm, int samples);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int rm_pipeline_read_packet(IntPtr h, byte* buf, int cap, uint* seq, ulong* captureTsUs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rm_pipeline_set_gain(IntPtr h, float db);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rm_pipeline_set_mute(IntPtr h, int muted);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rm_pipeline_set_ns(IntPtr h, int enabled);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rm_pipeline_set_agc(IntPtr h, int enabled);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rm_pipeline_set_bitrate(IntPtr h, int bps);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rm_pipeline_set_dtx(IntPtr h, int enabled);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rm_pipeline_get_stats(IntPtr h, out RmStats stats);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr rm_decoder_create(int sampleRate, int channels);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rm_decoder_destroy(IntPtr h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int rm_decoder_decode(IntPtr h, byte* data, int len, float* pcm, int maxSamples, int fec);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int rm_decoder_plc(IntPtr h, float* pcm, int samples);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr rm_monitor_create(int sampleRate, int channels);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern IntPtr rm_monitor_create_named(int sampleRate, int channels, string nameFilter);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int rm_monitor_write(IntPtr h, float* pcm, int samples);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rm_monitor_destroy(IntPtr h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr rm_capture_create(int sampleRate, int channels, IntPtr cb, IntPtr user);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rm_capture_start(IntPtr h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rm_capture_stop(IntPtr h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rm_capture_destroy(IntPtr h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rm_capture_set_polar(IntPtr h, int pattern);
}