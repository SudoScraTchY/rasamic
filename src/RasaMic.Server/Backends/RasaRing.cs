using System.IO.MemoryMappedFiles;

namespace RasaMic.Server.Backends;

/// <summary>
/// File-backed SPSC ring shared with the macOS HAL plugin (drivers/macos/RasaMicHalPlugin/rasa_ring.h).
/// Writer = server, reader = coreaudiod plugin. Positions are monotonic frame counters.
/// </summary>
public static class RasaRing
{
    public const uint Magic = 0x41534152;
    public const int Version = 1;
    public const int HeaderSize = 4096;
    public const int OffMagic = 0;
    public const int OffVersion = 4;
    public const int OffSampleRate = 8;
    public const int OffChannels = 12;
    public const int OffCapacity = 16;
    public const int OffWritePos = 24;
    public const int OffReadPos = 32;
    public const int OffStarted = 40;
}

public sealed unsafe class MmapRingWriter : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly long _capacity;
    private readonly int _channels;
    private long _writePos;
    private readonly DriftSlipPolicy? _drift;

    public MmapRingWriter(string path, int sampleRate, int channels, long capacityFrames = 65536, bool compensateDrift = false)
    {
        if ((capacityFrames & (capacityFrames - 1)) != 0)
            throw new ArgumentException("capacityFrames must be a power of two", nameof(capacityFrames));
        _capacity = capacityFrames;
        _channels = channels;
        _drift = compensateDrift ? new DriftSlipPolicy(sampleRate, capacityFrames) : null;
        long length = RasaRing.HeaderSize + capacityFrames * channels * sizeof(float);
        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        _file = MemoryMappedFile.CreateFromFile(fs, mapName: null, capacity: length, access: MemoryMappedFileAccess.ReadWrite, inheritability: HandleInheritability.None, leaveOpen: false);
        _view = _file.CreateViewAccessor(0, length, MemoryMappedFileAccess.ReadWrite);
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _base);
        *(uint*)(_base + RasaRing.OffMagic) = RasaRing.Magic;
        *(int*)(_base + RasaRing.OffVersion) = RasaRing.Version;
        *(int*)(_base + RasaRing.OffSampleRate) = sampleRate;
        *(int*)(_base + RasaRing.OffChannels) = channels;
        *(long*)(_base + RasaRing.OffCapacity) = capacityFrames;
        *(long*)(_base + RasaRing.OffWritePos) = 0;
        *(long*)(_base + RasaRing.OffReadPos) = 0;
        *(int*)(_base + RasaRing.OffStarted) = 1;
    }

    public long SamplesDropped => _drift?.SamplesDropped ?? 0;
    public long SamplesRepeated => _drift?.SamplesRepeated ?? 0;

    private void CompensateDrift()
    {
        if (_drift is null)
            return;
        long readPos = *(long*)(_base + RasaRing.OffReadPos);
        switch (_drift.Decide(_writePos - readPos))
        {
            case Slip.Drop:
                _writePos++; // skip one frame slot: reader never sees it
                break;
            case Slip.Repeat:
                var data = new Span<float>(_base + RasaRing.HeaderSize, (int)(_capacity * _channels));
                int prev = (int)((_writePos - 1) % _capacity) * _channels;
                int cur = (int)(_writePos % _capacity) * _channels;
                data.Slice(prev, _channels).CopyTo(data.Slice(cur, _channels));
                _writePos++;
                break;
        }
    }

    public void Write(ReadOnlySpan<float> pcm)
    {
        int frames = pcm.Length / _channels;
        if (frames == 0)
            return;
        CompensateDrift();
        var data = new Span<float>(_base + RasaRing.HeaderSize, (int)(_capacity * _channels));
        int index = (int)(_writePos % _capacity) * _channels;
        int first = Math.Min(frames * _channels, data.Length - index);
        pcm[..first].CopyTo(data[index..]);
        if (frames * _channels > first)
            pcm[first..].CopyTo(data);
        _writePos += frames;
        Thread.MemoryBarrier();
        *(long*)(_base + RasaRing.OffWritePos) = _writePos;
    }

    public void Dispose()
    {
        *(int*)(_base + RasaRing.OffStarted) = 0;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}

/// <summary>Reader side mirroring the HAL plugin's ReadInput; used by tests.</summary>
public sealed unsafe class MmapRingReader : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly long _capacity;
    private readonly int _channels;
    private long _readPos;

    public MmapRingReader(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        long length = fs.Length;
        _file = MemoryMappedFile.CreateFromFile(fs, mapName: null, capacity: length, access: MemoryMappedFileAccess.ReadWrite, inheritability: HandleInheritability.None, leaveOpen: false);
        _view = _file.CreateViewAccessor(0, length, MemoryMappedFileAccess.ReadWrite);
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _base);
        if (*(uint*)(_base + RasaRing.OffMagic) != RasaRing.Magic ||
            *(int*)(_base + RasaRing.OffVersion) != RasaRing.Version)
            throw new InvalidDataException("Not a Rasa ring file");
        SampleRate = *(int*)(_base + RasaRing.OffSampleRate);
        _channels = *(int*)(_base + RasaRing.OffChannels);
        _capacity = *(long*)(_base + RasaRing.OffCapacity);
        _readPos = *(long*)(_base + RasaRing.OffReadPos);
    }

    public int SampleRate { get; }
    public int Channels => _channels;

    /// <summary>Fills dest with up to dest.Length/channels frames; silence on underrun. Returns frames read.</summary>
    public int Read(Span<float> dest)
    {
        int frames = dest.Length / _channels;
        long writePos = *(long*)(_base + RasaRing.OffWritePos);
        Thread.MemoryBarrier();
        long available = writePos - _readPos;
        if (available <= 0)
        {
            dest.Clear();
            return 0;
        }
        if (available > _capacity)
            _readPos = writePos - _capacity; // writer lapped us: drop oldest
        int n = (int)Math.Min(frames, available);
        var data = new Span<float>(_base + RasaRing.HeaderSize, (int)(_capacity * _channels));
        int index = (int)(_readPos % _capacity) * _channels;
        int first = Math.Min(n * _channels, data.Length - index);
        data.Slice(index, first).CopyTo(dest);
        if (n * _channels > first)
            data[..(n * _channels - first)].CopyTo(dest[first..]);
        _readPos += n;
        *(long*)(_base + RasaRing.OffReadPos) = _readPos;
        if (n < frames)
            dest[(n * _channels)..].Clear();
        return n;
    }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}