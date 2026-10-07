using ILGPU;

namespace IndustrialVision.Filters.Gpu;

// ILGPU의 동적 launcher assembly가 값 형식에 접근해야 한다.
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public struct KernelSettings
{
    public int Width;
    public int Height;
    public int Radius;
    public int Vertical;
    public int Kind;
    public int HasDark;
    public float Scale;
    public float Minimum;
    public float Level;
}

internal static class CudaKernels
{
    private static bool Finite(float value) => value <= float.MaxValue && value >= -float.MaxValue;

    private static int Clamp(int value, int length) => value < 0 ? 0 : value >= length ? length - 1 : value;

    public static void Correct(Index1D i, ArrayView<float> input, ArrayView<float> output,
        ArrayView<float> map, ArrayView<float> dark, KernelSettings settings)
    {
        var offset = settings.HasDark != 0 ? dark[i] : 0;
        var denominator = map[i] - offset;
        var value = settings.Kind == 0
            ? (Finite(denominator) && denominator > settings.Minimum ? (input[i] - offset) / denominator * settings.Scale : float.NaN)
            : (map[i] > 0 ? (input[i] - offset) * map[i] * settings.Scale : float.NaN);
        output[i] = Finite(value) ? value : float.NaN;
    }

    public static void Convolve(Index1D i, ArrayView<float> input, ArrayView<float> output,
        ArrayView<float> kernel, KernelSettings settings)
    {
        var x = i.X % settings.Width;
        var y = i.X / settings.Width;
        float sum = 0;
        for (var k = -settings.Radius; k <= settings.Radius; k++)
        {
            var index = settings.Vertical != 0 ? (Clamp(y + k, settings.Height) * settings.Width) + x
                : (y * settings.Width) + Clamp(x + k, settings.Width);
            sum += input[index] * kernel[k + settings.Radius];
        }
        output[i] = Finite(sum) ? sum : float.NaN;
    }

    public static void Extrema(Index1D i, ArrayView<float> input, ArrayView<float> output, KernelSettings settings)
    {
        var x = i.X % settings.Width;
        var y = i.X / settings.Width;
        var value = settings.Kind != 0 ? -float.MaxValue : float.MaxValue;
        for (var k = -settings.Radius; k <= settings.Radius; k++)
        {
            var index = settings.Vertical != 0 ? (Clamp(y + k, settings.Height) * settings.Width) + x
                : (y * settings.Width) + Clamp(x + k, settings.Width);
            var sample = input[index];
            if (!Finite(sample))
            {
                output[i] = float.NaN;
                return;
            }
            value = settings.Kind != 0 ? (sample > value ? sample : value) : (sample < value ? sample : value);
        }
        output[i] = value;
    }

    public static void Subtract(Index1D i, ArrayView<float> input, ArrayView<float> output, KernelSettings settings)
    {
        var value = settings.Kind != 0 ? output[i] - input[i] : input[i] - output[i];
        output[i] = Finite(value) ? value : float.NaN;
    }

    public static void Threshold(Index1D i, ArrayView<float> input, ArrayView<float> output, KernelSettings settings)
    {
        output[i] = Finite(input[i]) ? (input[i] > settings.Level ? 255 : 0) : float.NaN;
    }
}
