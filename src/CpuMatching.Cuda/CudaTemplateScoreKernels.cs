using CpuMatching.Core;
using global::ILGPU;
using global::ILGPU.Algorithms;

namespace CpuMatching.Cuda;

/// <summary>ILGPU의 동적 launcher가 접근하는 값 타입. 픽셀 격자와 CPU SIMD 구간을 전달한다.</summary>
public readonly record struct CudaTemplateScoreGrid(int Width, int Height, int Offset, int Left, int VectorEnd);

/// <summary>한 GPU 스레드가 한 중심 위치를 평가한다. 표본의 합산 순서는 CPU와 같다.</summary>
internal static class CudaTemplateScoreKernels
{
    public static void Directions(Index1D index, ArrayView<float> maps, ArrayView<DirectionalSample> points,
        ArrayView<double> output, CudaTemplateScoreGrid grid, int rounded)
    {
        var pixel = grid.Offset + index;
        var x = pixel % grid.Width;
        var y = pixel / grid.Width;
        var planeSize = grid.Width * grid.Height;
        double sum = 0;
        float rowSum = 0;
        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            var px = x + point.X;
            var py = y + point.Y;
            if (px < 1 || py < 1 || px >= grid.Width - 2 || py >= grid.Height - 2)
            {
                output[pixel] = 0;
                return;
            }
            if (rounded != 0)
            {
                var sample = ((int)py * grid.Width) + (int)px;
                var first = maps[(point.Bin * planeSize) + sample];
                var second = maps[(point.Next * planeSize) + sample];
                // CPU SIMD 구간과 끝의 scalar 구간은 반올림 순서가 서로 다르다.
                if (x >= grid.Left && x < grid.VectorEnd)
                {
                    rowSum = rowSum + (first * (float)(1 - point.Fraction)) + (second * (float)point.Fraction);
                }
                else
                {
                    rowSum += (float)(((1 - point.Fraction) * first) + (point.Fraction * second));
                }
            }
            else
            {
                sum += MultiplyRounded(1 - point.Fraction, Bilinear(maps, point.Bin * planeSize, grid.Width, px, py)) +
                    MultiplyRounded(point.Fraction, Bilinear(maps, point.Next * planeSize, grid.Width, px, py));
            }
        }
        if (points.Length == 0)
        {
            output[pixel] = 0;
        }
        else if (rounded != 0)
        {
            output[pixel] = rowSum / (int)points.Length;
        }
        else
        {
            output[pixel] = sum / points.Length;
        }
    }

    public static void Correlation(Index1D index, ArrayView<byte> image, ArrayView<CorrelationSample> points,
        ArrayView<double> output, CudaTemplateScoreGrid grid, double templateEnergy)
    {
        var pixel = grid.Offset + index;
        if (templateEnergy < 1)
        {
            output[pixel] = 0;
            return;
        }
        var x = pixel % grid.Width;
        var y = pixel / grid.Width;
        double sum = 0;
        double squares = 0;
        double correlation = 0;
        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            var px = x + point.X;
            var py = y + point.Y;
            if (px < 0 || py < 0 || px >= grid.Width - 1 || py >= grid.Height - 1)
            {
                output[pixel] = 0;
                return;
            }
            var ix = (int)px;
            var iy = (int)py;
            var dx = px - ix;
            var dy = py - iy;
            var sample = (iy * grid.Width) + ix;
            var value = ((1 - dy) * (((1 - dx) * image[sample]) + (dx * image[sample + 1]))) +
                (dy * (((1 - dx) * image[sample + grid.Width]) + (dx * image[sample + grid.Width + 1])));
            sum += value;
            squares += value * value;
            correlation += point.Value * value;
        }
        var energy = squares - (sum * sum / points.Length);
        output[pixel] = energy < 1 ? 0 : XMath.Clamp(correlation / XMath.Sqrt(templateEnergy * energy), 0, 1);
    }

    private static float Bilinear(ArrayView<float> maps, int start, int width, double x, double y)
    {
        var ix = (int)x;
        var iy = (int)y;
        var dx = (float)(x - ix);
        var dy = (float)(y - iy);
        var index = start + (iy * width) + ix;
        var top = MultiplyRounded(1 - dx, maps[index]) + MultiplyRounded(dx, maps[index + 1]);
        var bottom = MultiplyRounded(1 - dx, maps[index + width]) + MultiplyRounded(dx, maps[index + width + 1]);
        return MultiplyRounded(1 - dy, top) + MultiplyRounded(dy, bottom);
    }

    // float 곱은 double에서 정확히 표현한 뒤 float로 반올림한다. GPU의 곱셈/덧셈 결합을 막아 CPU 보간과 맞춘다.
    private static float MultiplyRounded(float left, float right) => (float)((double)left * right);

    // 호출 경계로 double 곱셈도 먼저 반올림한다. 인라인하면 CUDA JIT가 뒤의 덧셈과 결합할 수 있다.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static double MultiplyRounded(double left, double right) => left * right;
}
