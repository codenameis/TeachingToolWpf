using VisionKernels;

namespace CpuMatching.Core;

public static class Edges
{
    public static CpuEdgePoint[] Extract(CpuGrayImage image, double centerX, double centerY, int maximum = 128)
    {
        var candidates = new List<CpuEdgePoint>();
        var gradients = Gradients(image);
        for (var y = 2; y < image.Height - 2; y++)
        {
            for (var x = 2; x < image.Width - 2; x++)
            {
                var index = (y * image.Width) + x;
                var (gx, gy) = gradients[index];
                var strength = Math.Sqrt((gx * gx) + (gy * gy));
                if (strength < 12)
                {
                    continue;
                }
                candidates.Add(new CpuEdgePoint(x - centerX, y - centerY, Math.Atan2(gy, gx), strength));
            }
        }
        var selected = new List<CpuEdgePoint>();
        foreach (var point in candidates.OrderByDescending(point => point.Strength))
        {
            var conflict = false;
            for (var i = 0; i < selected.Count; i++)
            {
                var other = selected[i];
                var dx = other.X - point.X;
                var dy = other.Y - point.Y;
                if ((dx * dx) + (dy * dy) < 4)
                {
                    conflict = true;
                    break;
                }
            }
            if (!conflict)
            {
                selected.Add(point);
                if (selected.Count == maximum)
                {
                    break;
                }
            }
        }
        return selected.ToArray();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static (double X, double Y)[] Gradients(CpuGrayImage image)
        => SobelKernel.ComputeNormalizedGray8Gradients(image.Pixels.Span, image.Width, image.Height);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static float Bilinear(float[] map, int width, int height, double x, double y)
        => BilinearSampler.SampleInteriorFloat(map, width, height, x, y);
}
