using System.Runtime.CompilerServices;

namespace VisionKernels;

/// <summary>
/// Bit-exact bilinear samplers preserving the exact clamping and floating-point parenthesization of each caller.
/// </summary>
public static class BilinearSampler
{
    /// <summary>
    /// Samples an 8-bit image plane with clamped right/bottom neighbor indices when <paramref name="x"/> == <c>width - 1</c> or <paramref name="y"/> == <c>height - 1</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double SampleClampedGray8(ReadOnlySpan<byte> pixels, int width, int height, double x, double y)
    {
        if (x < 0 || y < 0 || x > width - 1 || y > height - 1)
        {
            return 0;
        }

        var ix = (int)x;
        var iy = (int)y;
        var nx = Math.Min(ix + 1, width - 1);
        var ny = Math.Min(iy + 1, height - 1);
        var dx = x - ix;
        var dy = y - iy;
        return ((1 - dy) * (((1 - dx) * pixels[(iy * width) + ix]) + (dx * pixels[(iy * width) + nx]))) +
            (dy * (((1 - dx) * pixels[(ny * width) + ix]) + (dx * pixels[(ny * width) + nx])));
    }

    /// <summary>
    /// Samples a single-precision plane with strict interior bounds (<c>0 &lt;= x &lt; width - 1</c>, <c>0 &lt;= y &lt; height - 1</c>) and float weight interpolation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SampleInteriorFloat(ReadOnlySpan<float> map, int width, int height, double x, double y)
    {
        if (x < 0 || y < 0 || x >= width - 1 || y >= height - 1)
        {
            return 0;
        }

        var ix = (int)x;
        var iy = (int)y;
        var dx = (float)(x - ix);
        var dy = (float)(y - iy);
        var index = (iy * width) + ix;
        return ((1 - dy) * (((1 - dx) * map[index]) + (dx * map[index + 1]))) +
            (dy * (((1 - dx) * map[index + width]) + (dx * map[index + width + 1])));
    }

    /// <summary>
    /// Samples a single-precision plane with clamped right/bottom neighbor indices and double-precision weight products cast to <see cref="float"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SampleClampedFloatProductOrder(ReadOnlySpan<float> values, int width, int height, double x, double y)
    {
        if (x < 0 || y < 0 || x > width - 1 || y > height - 1)
        {
            return 0;
        }

        var ix = (int)x;
        var iy = (int)y;
        var jx = Math.Min(ix + 1, width - 1);
        var jy = Math.Min(iy + 1, height - 1);
        var tx = x - ix;
        var ty = y - iy;
        // [Perf Why]: Preserves exact left-to-right double product order so GradientImage edge NMS offsets match bit-for-bit.
        return (float)((values[(iy * width) + ix] * (1 - tx) * (1 - ty)) +
            (values[(iy * width) + jx] * tx * (1 - ty)) +
            (values[(jy * width) + ix] * (1 - tx) * ty) +
            (values[(jy * width) + jx] * tx * ty));
    }
}
