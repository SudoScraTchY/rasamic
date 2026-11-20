using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using RasaMic.Audio;
using RasaMic.Core.Jitter;
using RasaMic.Protocol;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

[MemoryDiagnoser]
public class PipelineBench
{
    private AudioPipeline _mono = null!;
    private AudioPipeline _lan = null!;
    private short[] _pcm480 = null!;
    private short[] _pcm960 = null!;
    private byte[] _packet = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mono = new AudioPipeline(new PipelineOptions { Bitrate = 32000 });
        _lan = new AudioPipeline(new PipelineOptions { Bitrate = 256000, Channels = 2, NoiseSuppression = true, Agc = true });
        _pcm480 = Voice(480);
        _pcm960 = Voice(960);
        _packet = new byte[2048];
        for (int i = 0; i < 50; i++) { Drain(_mono); Drain(_lan); }
    }

    private static short[] Voice(int n)
    {
        var pcm = new short[n];
        for (int i = 0; i < n; i++)
            pcm[i] = (short)(5000 * Math.Sin(2 * Math.PI * 180 * i / 48000)
                           + 3000 * Math.Sin(2 * Math.PI * 880 * i / 48000)
                           + 1500 * Math.Sin(2 * Math.PI * 2400 * i / 48000));
        return pcm;
    }

    private void Drain(AudioPipeline p)
    {
        p.Feed(p == _mono ? _pcm480 : _pcm960);
        while (p.ReadPacket(_packet, out _, out _) > 0) { }
    }

    [Benchmark] public void Feed_10ms_Mono_32k() => Drain(_mono);
    [Benchmark] public void Feed_10ms_Stereo_256k_NsAgc() => Drain(_lan);

    [GlobalCleanup]
    public void Cleanup() { _mono.Dispose(); _lan.Dispose(); }
}

[MemoryDiagnoser]
public class DecodeBench
{
    private OpusDecoder _decoder = null!;
    private byte[] _payload = null!;
    private float[] _pcm = null!;

    [GlobalSetup]
    public void Setup()
    {
        using var pipeline = new AudioPipeline(new PipelineOptions { NoiseSuppression = false, Agc = false });
        var pcm = new short[480];
        for (int i = 0; i < 480; i++) pcm[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 48000));
        var buf = new byte[2048];
        int len = 0;
        for (int i = 0; i < 10 && len == 0; i++)
        {
            pipeline.Feed(pcm);
            len = pipeline.ReadPacket(buf, out _, out _);
        }
        _payload = buf[..len];
        _decoder = new OpusDecoder();
        _pcm = new float[480];
    }

    [Benchmark] public int Decode_10ms() => _decoder.Decode(_payload, _pcm);
    [Benchmark] public int Plc_10ms() => _decoder.Plc(_pcm);

    [GlobalCleanup] public void Cleanup() => _decoder.Dispose();
}

[MemoryDiagnoser]
public class CryptoBench
{
    private AesGcmSealer _sealer = null!;
    private byte[] _plain = null!;
    private byte[] _nonce = null!;
    private byte[] _aad = null!;
    private byte[] _sealed = null!;
    private byte[] _out = null!;
    private int _sealedLen;

    [GlobalSetup]
    public void Setup()
    {
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        _sealer = new AesGcmSealer(key);
        _plain = new byte[320]; // 256 kbps stereo, 10 ms
        Random.Shared.NextBytes(_plain);
        _nonce = new byte[12];
        _aad = new byte[24];
        _sealed = new byte[336];
        _out = new byte[320];
        _sealedLen = _sealer.Seal(_plain, _nonce, _aad, _sealed);
    }

    [Benchmark] public int Seal_320B() => _sealer.Seal(_plain, _nonce, _aad, _sealed);
    [Benchmark] public int Open_320B() => _sealer.Open(_sealed.AsSpan(0, _sealedLen), _nonce, _aad, _out);

    [GlobalCleanup] public void Cleanup() => _sealer.Dispose();
}

[MemoryDiagnoser]
public class PostGainBench
{
    private float[] _pcm480 = null!;
    private float[] _pcm960 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _pcm480 = new float[480];
        _pcm960 = new float[960];
        for (int i = 0; i < 960; i++) _pcm960[i] = MathF.Sin(i * 0.01f) * 0.5f;
        Array.Copy(_pcm960, _pcm480, 480);
    }

    [Benchmark] public void Gain_480() => PcmPostGain.Apply(_pcm480, 1.5f);
    [Benchmark] public void Gain_960() => PcmPostGain.Apply(_pcm960, 1.5f);
}

[MemoryDiagnoser]
public class JitterBench
{
    private JitterBuffer _jb = null!;
    private byte[] _payload = null!;
    private uint _seq;

    [GlobalSetup]
    public void Setup()
    {
        _jb = new JitterBuffer(targetPackets: 2);
        _payload = new byte[80];
        for (uint i = 0; i < 4; i++) _jb.Push(i, _payload);
        _seq = 4;
    }

    [Benchmark]
    public void PushNext_Steady()
    {
        _jb.Push(_seq++, _payload);
        _jb.Next(out _, out _, out _);
    }

    [Benchmark]
    public void PushNext_10pctLoss()
    {
        _jb.Push(_seq++, _payload);
        if (_seq % 10 == 0) _seq++; // gap: PLC path
        _jb.Next(out _, out _, out _);
    }
}