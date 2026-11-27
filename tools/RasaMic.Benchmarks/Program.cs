using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using RasaMic.Audio;
using RasaMic.Core.Jitter;
using RasaMic.Protocol;
using System.Buffers.Binary;
using System.Runtime.Intrinsics;

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

[MemoryDiagnoser]
public class PostGainApproachesBench
{
    private float[] _source = null!;
    private float[] _destination = null!;

    [Params(480, 960)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        _source = new float[Length];
        _destination = new float[Length];
        for (int i = 0; i < Length; i++)
            _source[i] = (float)(rng.NextDouble() * 4 - 2);
    }

    [Benchmark(Baseline = true)]
    public void TensorTwoPass() => TensorTwoPass(_source, _destination, 1.5f);

    [Benchmark]
    public void Scalar() => ScalarApply(_source, _destination, 1.5f);

    [Benchmark]
    public void Fused_Vector128() => Fused128(_source, _destination, 1.5f);

    [Benchmark]
    public void Fused_Vector256() => Fused256(_source, _destination, 1.5f);

    private static void TensorTwoPass(ReadOnlySpan<float> source, Span<float> destination, float gain)
    {
        System.Numerics.Tensors.TensorPrimitives.Multiply(source, gain, destination);
        System.Numerics.Tensors.TensorPrimitives.Clamp(destination, -1f, 1f, destination);
    }

    private static void ScalarApply(ReadOnlySpan<float> source, Span<float> destination, float gain)
    {
        for (int i = 0; i < source.Length; i++)
            destination[i] = Math.Clamp(source[i] * gain, -1f, 1f);
    }

    private static void Fused128(ReadOnlySpan<float> source, Span<float> destination, float gain)
    {
        int width = Vector128<float>.Count;
        if (source.Length < width)
        {
            ScalarApply(source, destination, gain);
            return;
        }

        Vector128<float> gainVector = Vector128.Create(gain);
        Vector128<float> low = Vector128.Create(-1f);
        Vector128<float> high = Vector128.Create(1f);
        int last = source.Length - width;
        int i = 0;
        for (; i <= last; i += width)
            Transform(Vector128.Create(source.Slice(i)), gainVector, low, high).CopyTo(destination.Slice(i));

        if (i < source.Length)
        {
            int skip = i - last;
            Vector128<float> original = Vector128.Create(destination.Slice(last));
            Vector128<float> values = Vector128.Create(source.Slice(last));
            Vector128<float> mask = Vector128.GreaterThanOrEqual(Vector128<int>.Indices, Vector128.Create(skip)).As<int, float>();
            Vector128.ConditionalSelect(mask, Transform(values, gainVector, low, high), original).CopyTo(destination.Slice(last));
        }
    }

    private static void Fused256(ReadOnlySpan<float> source, Span<float> destination, float gain)
    {
        int width = Vector256<float>.Count;
        if (!Vector256.IsHardwareAccelerated || source.Length < width)
        {
            Fused128(source, destination, gain);
            return;
        }

        Vector256<float> gainVector = Vector256.Create(gain);
        Vector256<float> low = Vector256.Create(-1f);
        Vector256<float> high = Vector256.Create(1f);
        int last = source.Length - width;
        int i = 0;
        for (; i <= last; i += width)
            Transform(Vector256.Create(source.Slice(i)), gainVector, low, high).CopyTo(destination.Slice(i));

        if (i < source.Length)
        {
            int skip = i - last;
            Vector256<float> original = Vector256.Create(destination.Slice(last));
            Vector256<float> values = Vector256.Create(source.Slice(last));
            Vector256<float> mask = Vector256.GreaterThanOrEqual(Vector256<int>.Indices, Vector256.Create(skip)).As<int, float>();
            Vector256.ConditionalSelect(mask, Transform(values, gainVector, low, high), original).CopyTo(destination.Slice(last));
        }
    }

    private static Vector128<float> Transform(Vector128<float> value, Vector128<float> gain, Vector128<float> low, Vector128<float> high)
    {
        value *= gain;
        value = Vector128.ConditionalSelect(Vector128.LessThan(value, low), low, value);
        return Vector128.ConditionalSelect(Vector128.GreaterThan(value, high), high, value);
    }

    private static Vector256<float> Transform(Vector256<float> value, Vector256<float> gain, Vector256<float> low, Vector256<float> high)
    {
        value *= gain;
        value = Vector256.ConditionalSelect(Vector256.LessThan(value, low), low, value);
        return Vector256.ConditionalSelect(Vector256.GreaterThan(value, high), high, value);
    }
}

[MemoryDiagnoser]
public class AsyncFramingBench
{
    private MemoryStream _synchronous = null!;
    private YieldingMemoryStream _suspended = null!;

    [GlobalSetup]
    public void Setup()
    {
        byte[] frame = new byte[322];
        frame[0] = 1;
        frame[1] = 64;
        new Random(42).NextBytes(frame.AsSpan(2));
        _synchronous = new MemoryStream(frame);
        _suspended = new YieldingMemoryStream(frame);
    }

    [Benchmark(Baseline = true)]
    public Task<byte[]?> Task_Synchronous() => TaskReadRawAsync(Reset(_synchronous));

    [Benchmark]
    public ValueTask<byte[]?> ValueTask_Synchronous() => ControlFraming.ReadRawAsync(Reset(_synchronous));

    [Benchmark]
    public Task<byte[]?> Task_Suspended() => TaskReadRawAsync(Reset(_suspended));

    [Benchmark]
    public ValueTask<byte[]?> ValueTask_Suspended() => ControlFraming.ReadRawAsync(Reset(_suspended));

    private static Stream Reset(MemoryStream stream)
    {
        stream.Position = 0;
        return stream;
    }

    private static async Task<byte[]?> TaskReadRawAsync(Stream stream)
    {
        byte[] lengthPrefix = new byte[2];
        if (!await TaskReadExactAsync(stream, lengthPrefix).ConfigureAwait(false))
            return null;
        int length = BinaryPrimitives.ReadUInt16BigEndian(lengthPrefix);
        byte[] payload = new byte[length];
        if (!await TaskReadExactAsync(stream, payload).ConfigureAwait(false))
            throw new EndOfStreamException("Truncated control frame.");
        return payload;
    }

    private static async Task<bool> TaskReadExactAsync(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset)).ConfigureAwait(false);
            if (read == 0)
                return offset == 0 && buffer.Length == 2 ? false : throw new EndOfStreamException("Truncated stream.");
            offset += read;
        }
        return true;
    }

    private sealed class YieldingMemoryStream(byte[] buffer) : MemoryStream(buffer)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}