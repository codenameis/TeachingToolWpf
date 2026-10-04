using global::ILGPU;
using global::ILGPU.Algorithms;

namespace VisionCompute.ILGPU;

internal static class CudaGradientKernels
{
    public static void Horizontal(Index1D index, ArrayView<byte> input, ArrayView<float> output, int width, int height)
    {
        var i = (int)index;
        if (i >= width * height)
        {
            return;
        }
        var x = i % width;
        var row = i - x;
        var sum = 0f;
        for (var k = -2; k <= 2; k++)
        {
            sum += Weight(k) * input[row + XMath.Clamp(x + k, 0, width - 1)];
        }
        output[i] = sum / 16;
    }

    public static void Vertical(Index1D index, ArrayView<float> input, ArrayView<float> output, int width, int height)
    {
        var i = (int)index;
        if (i >= width * height)
        {
            return;
        }
        var x = i % width;
        var y = i / width;
        var sum = 0f;
        for (var k = -2; k <= 2; k++)
        {
            sum += Weight(k) * input[(XMath.Clamp(y + k, 0, height - 1) * width) + x];
        }
        output[i] = sum / 16;
    }

    private static int Weight(int offset) => offset == 0 ? 6 : offset is -1 or 1 ? 4 : 1;

    public static void Sobel(Index1D index, ArrayView<float> smooth, ArrayView<float> derivativeX,
        ArrayView<float> derivativeY, int width, int height)
    {
        var i = (int)index;
        if (i >= width * height)
        {
            return;
        }
        derivativeX[i] = 0;
        derivativeY[i] = 0;
        var x = i % width;
        var y = i / width;
        if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
        {
            return;
        }
        var dx = (smooth[i - width + 1] + (2 * smooth[i + 1]) + smooth[i + width + 1] -
            smooth[i - width - 1] - (2 * smooth[i - 1]) - smooth[i + width - 1]) / 8;
        var dy = (smooth[i + width - 1] + (2 * smooth[i + width]) + smooth[i + width + 1] -
            smooth[i - width - 1] - (2 * smooth[i - width]) - smooth[i - width + 1]) / 8;
        derivativeX[i] = dx;
        derivativeY[i] = dy;
    }
}
