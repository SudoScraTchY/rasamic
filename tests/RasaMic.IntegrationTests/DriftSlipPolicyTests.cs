using RasaMic.Server.Backends;
using Xunit;

namespace RasaMic.IntegrationTests;

public class DriftSlipPolicyTests
{
    [Fact]
    public void Overfull_DropsOnce_ThenRateLimits()
    {
        var p = new DriftSlipPolicy(48000, 4096); // target 960, band 480
        Assert.Equal(Slip.Drop, p.Decide(2000));
        Assert.Equal(Slip.None, p.Decide(2000)); // <10 s since last slip
        Assert.Equal(1, p.SamplesDropped);
        Assert.Equal(0, p.SamplesRepeated);
    }

    [Fact]
    public void Starving_RepeatsLastFrame()
    {
        var p = new DriftSlipPolicy(48000, 4096);
        Assert.Equal(Slip.Repeat, p.Decide(100));
        Assert.Equal(Slip.None, p.Decide(100));
        Assert.Equal(1, p.SamplesRepeated);
        Assert.Equal(0, p.SamplesDropped);
    }

    [Fact]
    public void InsideBand_NoSlip()
    {
        var p = new DriftSlipPolicy(48000, 4096);
        Assert.Equal(Slip.None, p.Decide(960));
        Assert.Equal(Slip.None, p.Decide(1400)); // target + band edge
        Assert.Equal(Slip.None, p.Decide(500));  // target - band edge
    }

    [Fact]
    public void IdleOrLapped_NoSlip()
    {
        var p = new DriftSlipPolicy(48000, 4096);
        Assert.Equal(Slip.None, p.Decide(0));
        Assert.Equal(Slip.None, p.Decide(-5));
        Assert.Equal(Slip.None, p.Decide(4097));
        Assert.Equal(0, p.SamplesDropped);
        Assert.Equal(0, p.SamplesRepeated);
    }
}