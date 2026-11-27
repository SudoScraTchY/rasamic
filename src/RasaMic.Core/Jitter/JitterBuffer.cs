namespace RasaMic.Core.Jitter;

public enum JitterResult
{
    Empty,
    Packet,
    Gap,
}

/// <summary>
/// Adaptive packet jitter buffer. Reorders by seq, emits Gap for missing packets
/// (caller runs Opus PLC/FEC), grows target depth on late loss, shrinks when stable.
/// Single consumer; Push may come from a different thread than Next (lock-free via lock).
/// </summary>
public sealed class JitterBuffer
{
    private const int Slots = 256;
    private const int MaxPacketSize = 512;
    private const int MaxGapBeforeResync = 32;
    private const int StableShrinkMs = 1000;

    private readonly byte[][] _payloads = new byte[Slots][];
    private readonly int[] _lens = new int[Slots];
    private readonly bool[] _filled = new bool[Slots];
    private readonly uint[] _seqs = new uint[Slots];
    private readonly long[] _pushTs = new long[Slots];
    private static readonly double TickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    private readonly object _gate = new();

    private uint _nextSeq;
    private bool _started;
    private bool _playing;
    private int _target;
    private long _lastLossTick;
    private long _lastPushTick;
    private long _stableSinceTick;

    public JitterBuffer(int targetPackets = 1, int minPackets = 1, int maxPackets = 4)
    {
        _target = Math.Clamp(targetPackets, minPackets, maxPackets);
        MinPackets = minPackets;
        MaxPackets = maxPackets;
        _stableSinceTick = Environment.TickCount64;
        for (int i = 0; i < Slots; i++)
            _payloads[i] = new byte[MaxPacketSize];
    }

    public int MinPackets { get; }
    public int MaxPackets { get; }
    public int TargetPackets { get { lock (_gate) return _target; } }
    public int Depth { get; private set; }

    /// <summary>Time the last emitted packet spent in the buffer (push → next).</summary>
    public double LastDwellMs { get; private set; }

    public void Push(uint seq, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPacketSize)
            return;
        lock (_gate)
        {
            long now = Environment.TickCount64;
            if (!_started)
            {
                _started = true;
                _nextSeq = seq;
                _lastPushTick = now;
            }

            if (unchecked((int)(seq - _nextSeq)) < 0)
            {
                // Late packet: its slot was already consumed (PLC ran). Adapt up.
                _lastLossTick = Environment.TickCount64;
                _stableSinceTick = _lastLossTick;
                if (_target < MaxPackets)
                    _target++;
                return;
            }

            if (seq - _nextSeq > MaxGapBeforeResync && now - _lastPushTick > 100)
            {
                Array.Clear(_filled);
                Depth = 0;
                _nextSeq = seq;
                _playing = false;
                _target = Math.Min(_target, 2);
                _lastLossTick = now;
                _stableSinceTick = now;
            }
            _lastPushTick = now;

            int idx = (int)(seq % Slots);
            if (_filled[idx] && _seqs[idx] != seq)
                return; // slot occupied by a much newer packet; drop
            if (!_filled[idx])
                Depth++;
            _filled[idx] = true;
            _seqs[idx] = seq;
            _pushTs[idx] = System.Diagnostics.Stopwatch.GetTimestamp();
            _lens[idx] = payload.Length;
            payload.CopyTo(_payloads[idx]);
        }
    }

    public JitterResult Next(out uint seq, out byte[] payload, out int len)
    {
        lock (_gate)
        {
            seq = 0;
            payload = Array.Empty<byte>();
            len = 0;
            if (!_started)
                return JitterResult.Empty;
            if (!_playing)
            {
                if (Depth < _target)
                    return JitterResult.Empty;
                _playing = true;
            }
            if (Depth == 0)
            {
                _playing = false; // underrun: rebuffer to target before resuming
                return JitterResult.Empty;
            }

            int idx = (int)(_nextSeq % Slots);
            if (_filled[idx] && _seqs[idx] == _nextSeq)
            {
                _filled[idx] = false;
                Depth--;
                seq = _nextSeq++;
                payload = _payloads[idx];
                len = _lens[idx];
                LastDwellMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _pushTs[idx]) * TickMs;

                long now = Environment.TickCount64;
                if (_target > MinPackets && now - _stableSinceTick > StableShrinkMs)
                {
                    _target--;
                    _stableSinceTick = now;
                }
                return JitterResult.Packet;
            }

            // Expected packet missing but buffer is deep enough: emit a gap for PLC.
            _nextSeq++;
            _lastLossTick = Environment.TickCount64;
            _stableSinceTick = _lastLossTick;
            return JitterResult.Gap;
        }
    }
}