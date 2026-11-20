using Android.Media;
using RasaMic.Audio;

namespace RasaMic.Client;

/// <summary>
/// Optional Java-side capture (M7): AudioRecord exposes mic direction
/// (SetPreferredMicrophoneDirection, API 29+) which AAudio/Oboe cannot reach.
/// Higher latency than the Oboe LowLatency path — opt-in via the HD capture
/// switch. VOICE_RECOGNITION preset keeps platform AGC/NS out of our chain.
/// </summary>
public sealed class AudioRecordCapture : ICaptureSource
{
    private readonly Action<ReadOnlySpan<short>> _onPcm;
    private readonly int _direction;
    private AudioRecord? _record;
    private Thread? _thread;
    private volatile bool _running;

    public AudioRecordCapture(Action<ReadOnlySpan<short>> onPcm, int direction)
    {
        _onPcm = onPcm;
        _direction = direction;
    }

    public void Start()
    {
        int minBuf = AudioRecord.GetMinBufferSize(48000, ChannelIn.Mono, Encoding.Pcm16bit);
        if (minBuf <= 0)
            throw new InvalidOperationException("48 kHz mono PCM16 capture is not supported on this device.");
        _record = new AudioRecord(AudioSource.VoiceRecognition, 48000, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(minBuf, 9600));
        if (_record.State != State.Initialized)
        {
            _record.Release();
            _record = null;
            throw new InvalidOperationException("AudioRecord initialization failed.");
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(29) && _direction > 0)
            _record.SetPreferredMicrophoneDirection((MicDirection)_direction);
        _running = true;
        _record.StartRecording();
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "rasa-audiorecord" };
        _thread.Start();
    }

    private void ReadLoop()
    {
        short[] buf = new short[480];
        while (_running)
        {
            int n = _record!.Read(buf, 0, buf.Length);
            if (n > 0)
                _onPcm(buf.AsSpan(0, n));
        }
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
        if (_record is not null)
        {
            try { _record.Stop(); } catch (InvalidOperationException) { }
            _record.Release();
            _record = null;
        }
    }

    public void Dispose() => Stop();
}