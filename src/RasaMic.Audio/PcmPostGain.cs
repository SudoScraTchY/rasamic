using System.Numerics.Tensors;

namespace RasaMic.Audio;

/// <summary>
/// Server-side post-gain with a hard safety limiter. TensorPrimitives lowers to
/// SIMD on all supported platforms; in-place, zero-allocation, any length.
/// </summary>
public static class PcmPostGain
{
    public static void Apply(Span<float> pcm, float gain)
    {
        TensorPrimitives.Multiply(pcm, gain, pcm);
        TensorPrimitives.Clamp(pcm, -1f, 1f, pcm);
    }
}