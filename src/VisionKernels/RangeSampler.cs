namespace VisionKernels;

/// <summary>
/// Uniform closed-interval grid sampling shared across pyramid template matchers.
/// </summary>
public static class RangeSampler
{
    public static int FillSamples(double minimum, double maximum, double step, Span<double> buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        var count = (int)Math.Ceiling((maximum - minimum) / step);
        var total = count + 1;
        for (var i = 0; i <= count && i < buffer.Length; i++)
        {
            buffer[i] = Math.Min(maximum, minimum + (i * step));
        }

        return Math.Min(total, buffer.Length);
    }

    public static IEnumerable<double> EnumerateSamples(double minimum, double maximum, double step)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        var count = (int)Math.Ceiling((maximum - minimum) / step);
        for (var i = 0; i <= count; i++)
        {
            yield return Math.Min(maximum, minimum + (i * step));
        }
    }
}
