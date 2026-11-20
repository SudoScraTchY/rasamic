namespace RasaMic.Server.Backends;

/// <summary>
/// macOS virtual microphone: writes decoded PCM into the mmap ring read by the
/// RasaMicHalPlugin.driver bundle (coreaudiod). Install the plugin to
/// /Library/Audio/Plug-Ins/HAL and restart coreaudiod once (see drivers/macos).
/// </summary>
public sealed class MacHalBackend : IVirtualMicBackend
{
    public const string DefaultRingPath = "/Library/Application Support/RasaMic/ring.bin";

    private readonly string _ringPath;
    private MmapRingWriter? _writer;

    public MacHalBackend(string? ringPath = null)
    {
        _ringPath = ringPath ?? DefaultRingPath;
    }

    public string Name => "macOS HAL virtual mic";

    public void Start(int sampleRate, int channels)
    {
        Stop();
        string? dir = Path.GetDirectoryName(_ringPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _writer = new MmapRingWriter(_ringPath, sampleRate, channels, compensateDrift: true);
        if (!OperatingSystem.IsWindows())
        {
            // coreaudiod runs as a different user and must write read_pos back.
            File.SetUnixFileMode(_ringPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        }
    }

    public void Write(ReadOnlySpan<float> pcm) => _writer?.Write(pcm);

    public void Stop()
    {
        _writer?.Dispose();
        _writer = null;
    }

    public void Dispose() => Stop();
}