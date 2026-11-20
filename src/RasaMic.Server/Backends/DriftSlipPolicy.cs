namespace RasaMic.Server.Backends;

/// <summary>
/// Clock-drift slip policy shared by the virtual-mic ring writers: keep the
/// ring fill near a 20 ms target by slipping one frame at a time (drop when
/// overfull, repeat when starving), normally ≤1 per 10 s. Near overflow or
/// underrun the interval tightens to 1 s so the ring never laps.
/// </summary>
public sealed class DriftSlipPolicy
{
    private readonly long _capacity;
    private readonly long _targetFill;
    private readonly long _band;
    private long _lastSlipTs;

    public DriftSlipPolicy(int sampleRate, long capacityFrames)
    {
        _capacity = capacityFrames;
        _targetFill = Math.Min(sampleRate / 50, capacityFrames / 4); // 20 ms
        _band = Math.Max(sampleRate / 100, 1); // 10 ms hysteresis
    }

    public long SamplesDropped { get; private set; }
    public long SamplesRepeated { get; private set; }

    /// <summary>fill = producer position − consumer position, in frames.</summary>
    public Slip Decide(long fill)
    {
        if (fill <= 0 || fill > _capacity)
            return Slip.None; // reader idle/keeping up exactly, or already lapped
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool emergency = fill > _capacity * 3 / 4 || fill < _targetFill / 2;
        long minInterval = System.Diagnostics.Stopwatch.Frequency * (emergency ? 1 : 10);
        if (now - _lastSlipTs < minInterval)
            return Slip.None;
        if (fill > _targetFill + _band)
        {
            SamplesDropped++;
            _lastSlipTs = now;
            return Slip.Drop;
        }
        if (fill < _targetFill - _band)
        {
            SamplesRepeated++;
            _lastSlipTs = now;
            return Slip.Repeat;
        }
        return Slip.None;
    }
}

public enum Slip
{
    None,
    Drop,
    Repeat,
}