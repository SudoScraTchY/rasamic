namespace RasaMic.Audio;

public sealed record PipelineOptions
{
    public int SampleRate { get; init; } = 48000;
    public int FrameMs { get; init; } = 10;
    public int Bitrate { get; init; } = 32000;
    public bool NoiseSuppression { get; init; } = true;
    public bool Agc { get; init; } = true;
    public float GainDb { get; init; }
    public bool Muted { get; init; }
    public bool Fec { get; init; } = true;
    public bool Dtx { get; init; }
    public int Channels { get; init; } = 1;
}

/// <summary>
/// Managed wrapper over the native capture→DSP→Opus pipeline.
/// Feed PCM from any capture backend; drain encoded packets on the sender thread.
/// </summary>
public sealed class AudioPipeline : IDisposable
{
    private IntPtr _handle;

    public AudioPipeline(PipelineOptions options)
    {
        FrameMs = options.FrameMs;
        var cfg = new RmConfig
        {
            SampleRate = options.SampleRate,
            FrameMs = options.FrameMs,
            Bitrate = options.Bitrate,
            NsEnabled = options.NoiseSuppression ? 1 : 0,
            AgcEnabled = options.Agc ? 1 : 0,
            GainDb = options.GainDb,
            Muted = options.Muted ? 1 : 0,
            FecEnabled = options.Fec ? 1 : 0,
            DtxEnabled = options.Dtx ? 1 : 0,
            Channels = options.Channels,
        };
        _handle = NativeMethods.rm_pipeline_create(cfg);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create native audio pipeline (check sample rate / frame size).");
    }

    public int FrameMs { get; }
    public int FrameSamples => FrameMs * 48;

    public unsafe int Feed(ReadOnlySpan<short> pcm)
    {
        fixed (short* p = pcm)
            return NativeMethods.rm_pipeline_feed(_handle, p, pcm.Length);
    }

    /// <summary>Reads one encoded packet into <paramref name="buffer"/>; returns length, 0 when empty.</summary>
    public unsafe int ReadPacket(Span<byte> buffer, out uint seq, out ulong captureTsUs)
    {
        uint s = 0;
        ulong ts = 0;
        int len;
        fixed (byte* b = buffer)
            len = NativeMethods.rm_pipeline_read_packet(_handle, b, buffer.Length, &s, &ts);
        seq = s;
        captureTsUs = ts;
        return len;
    }

    public void SetGain(float db) => NativeMethods.rm_pipeline_set_gain(_handle, db);
    public void SetMute(bool muted) => NativeMethods.rm_pipeline_set_mute(_handle, muted ? 1 : 0);
    public void SetNoiseSuppression(bool enabled) => NativeMethods.rm_pipeline_set_ns(_handle, enabled ? 1 : 0);
    public void SetAgc(bool enabled) => NativeMethods.rm_pipeline_set_agc(_handle, enabled ? 1 : 0);
    public void SetBitrate(int bps) => NativeMethods.rm_pipeline_set_bitrate(_handle, bps);
    public void SetDtx(bool enabled) => NativeMethods.rm_pipeline_set_dtx(_handle, enabled ? 1 : 0);

    public void Flush() => NativeMethods.rm_pipeline_flush(_handle);

    public (ulong Frames, ulong Packets, ulong Dropped) GetStats()
    {
        NativeMethods.rm_pipeline_get_stats(_handle, out RmStats stats);
        return (stats.FramesProcessed, stats.PacketsProduced, stats.PacketsDropped);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.rm_pipeline_destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

/// <summary>Server-side Opus decoder (float output).</summary>
public sealed class OpusDecoder : IDisposable
{
    private readonly int _channels;
    private IntPtr _handle;

    public OpusDecoder(int sampleRate = 48000, int channels = 1)
    {
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels));
        _channels = channels;
        _handle = NativeMethods.rm_decoder_create(sampleRate, channels);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to create native Opus decoder.");
    }

    /// <summary>Decodes a packet to float PCM normalized to [-1, 1] (int16 / 32768).
    /// With <paramref name="fec"/>=true recovers the previous lost frame (LBRR).</summary>
    public unsafe int Decode(ReadOnlySpan<byte> payload, Span<float> pcm, bool fec = false)
    {
        if (pcm.Length % _channels != 0)
            throw new ArgumentException("PCM buffer must contain complete interleaved channel frames.", nameof(pcm));
        fixed (byte* d = payload)
        fixed (float* p = pcm)
            return NativeMethods.rm_decoder_decode(_handle, d, payload.Length, p, pcm.Length / _channels, fec ? 1 : 0);
    }

    /// <summary>Packet-loss concealment for a missing packet.</summary>
    public unsafe int Plc(Span<float> pcm)
    {
        if (pcm.Length % _channels != 0)
            throw new ArgumentException("PCM buffer must contain complete interleaved channel frames.", nameof(pcm));
        fixed (float* p = pcm)
            return NativeMethods.rm_decoder_plc(_handle, p, pcm.Length / _channels);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.rm_decoder_destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }
}