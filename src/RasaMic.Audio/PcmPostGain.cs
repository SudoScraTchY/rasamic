using System.Numerics.Tensors;
using System.Runtime.Intrinsics;

namespace RasaMic.Audio;

/// <summary>
/// Server-side post-gain with a hard safety limiter. TensorPrimitives lowers to
/// SIMD on all supported platforms; in-place, zero-allocation, any length.
/// </summary>
public static class PcmPostGain
{
    public static void Apply(Span<float> pcm, float gain)
    {
        if (Vector256.IsHardwareAccelerated && pcm.Length >= Vector256<float>.Count)
        {
            ApplyVector256(pcm, gain);
            return;
        }

        TensorPrimitives.Multiply(pcm, gain, pcm);
        TensorPrimitives.Clamp(pcm, -1f, 1f, pcm);
    }

    private static void ApplyVector256(Span<float> pcm, float gain)
    {
        int width = Vector256<float>.Count;
        Vector256<float> gainVector = Vector256.Create(gain);
        Vector256<float> low = Vector256.Create(-1f);
        Vector256<float> high = Vector256.Create(1f);
        int last = pcm.Length - width;
        int i = 0;
        for (; i <= last; i += width)
            Transform(Vector256.Create(pcm.Slice(i)), gainVector, low, high).CopyTo(pcm.Slice(i));

        if (i < pcm.Length)
        {
            int skip = i - last;
            Vector256<float> tail = Vector256.Create(pcm.Slice(last));
            Vector256<float> mask = Vector256.GreaterThanOrEqual(Vector256<int>.Indices, Vector256.Create(skip)).As<int, float>();
            Vector256.ConditionalSelect(mask, Transform(tail, gainVector, low, high), tail).CopyTo(pcm.Slice(last));
        }
    }

    private static Vector256<float> Transform(Vector256<float> value, Vector256<float> gain, Vector256<float> low, Vector256<float> high)
    {
        value *= gain;
        // Comparisons keep NaN and signed zero intact, matching Math.Clamp.
        value = Vector256.ConditionalSelect(Vector256.LessThan(value, low), low, value);
        return Vector256.ConditionalSelect(Vector256.GreaterThan(value, high), high, value);
    }
}