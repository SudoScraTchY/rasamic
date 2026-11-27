using RasaMic.Audio;
using Xunit;

namespace RasaMic.Audio.Tests;

public class PcmPostGainTests
{
    private static void ScalarApply(float[] pcm, float gain)
    {
        for (int i = 0; i < pcm.Length; i++)
            pcm[i] = Math.Clamp(pcm[i] * gain, -1f, 1f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(479)]
    [InlineData(480)]
    [InlineData(481)]
    [InlineData(960)]
    public void MatchesScalarContract(int len)
    {
        var rng = new Random(42);
        float[] actual = new float[len];
        float[] expected = new float[len];
        for (int i = 0; i < len; i++)
        {
            float v = (float)(rng.NextDouble() * 4 - 2); // includes values beyond [-1,1]
            actual[i] = v;
            expected[i] = v;
        }
        PcmPostGain.Apply(actual, 1.5f);
        ScalarApply(expected, 1.5f);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnityGain_LeavesInRangeSamplesUnchanged()
    {
        float[] pcm = { -1f, -0.5f, 0f, 0.25f, 1f };
        float[] copy = (float[])pcm.Clone();
        PcmPostGain.Apply(pcm, 1f);
        Assert.Equal(copy, pcm);
    }

    [Fact]
    public void HotSignal_ClampedToFullScale()
    {
        float[] pcm = { 0.9f, -0.9f, 0.9f, -0.9f, 0.9f };
        PcmPostGain.Apply(pcm, 4f); // +12 dB
        Assert.All(pcm, v => Assert.True(Math.Abs(v) <= 1f));
        Assert.Equal(1f, pcm[0]);
        Assert.Equal(-1f, pcm[1]);
    }

    [Fact]
    public void SixDb_RoughlyDoublesAmplitude()
    {
        float[] pcm = Enumerable.Repeat(0.2f, 480).ToArray();
        PcmPostGain.Apply(pcm, MathF.Pow(10f, 6f / 20f));
        Assert.All(pcm, v => Assert.InRange(v, 0.39f, 0.41f));
    }

    [Fact]
    public void EmptySpan_NoThrow() => PcmPostGain.Apply(Span<float>.Empty, 2f);

    [Fact]
    public void SpecialValues_MatchScalarContract()
    {
        float[] pcm = [float.NaN, 0f, -0f, float.PositiveInfinity, float.NegativeInfinity, 2f, -2f, 0.25f, -0.25f];
        int positiveZeroBits = BitConverter.SingleToInt32Bits(0f);
        int negativeZeroBits = BitConverter.SingleToInt32Bits(-0f);

        PcmPostGain.Apply(pcm, 1.5f);

        Assert.True(float.IsNaN(pcm[0]));
        Assert.Equal(positiveZeroBits, BitConverter.SingleToInt32Bits(pcm[1]));
        Assert.Equal(negativeZeroBits, BitConverter.SingleToInt32Bits(pcm[2]));
        Assert.Equal(1f, pcm[3]);
        Assert.Equal(-1f, pcm[4]);
        Assert.Equal(1f, pcm[5]);
        Assert.Equal(-1f, pcm[6]);
    }

    [Fact]
    public void NonZeroOffsetSlice_DoesNotTouchSurroundingData()
    {
        const int length = 481;
        float[] input = new float[length];
        float[] expected = new float[length];
        for (int i = 0; i < length; i++)
            input[i] = expected[i] = i / 1000f;
        ScalarApply(expected, 1.5f);
        float[] buffer = Enumerable.Repeat(123f, length + 4).ToArray();
        input.CopyTo(buffer, 2);

        PcmPostGain.Apply(buffer.AsSpan(2, length), 1.5f);

        Assert.Equal(123f, buffer[0]);
        Assert.Equal(123f, buffer[1]);
        Assert.Equal(123f, buffer[^2]);
        Assert.Equal(123f, buffer[^1]);
        Assert.Equal(expected, buffer.AsSpan(2, length).ToArray());
    }
}