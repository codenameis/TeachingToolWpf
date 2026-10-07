using IndustrialVision.Filters.Core;

namespace IndustrialVision.Filters.Examples;

public static class SyntheticExample
{
    public static HostImage Create(int width, int height, PixelFormat format)
    {
        var image = new HostImage(new(width, height, format, exposureKey: "synthetic-fixed"));
        using var pixels = new PooledBuffer<float>(checked(width * height));
        var maximum = format == PixelFormat.Gray8 ? 255f : 65535f;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var background = maximum * (0.4f + (0.15f * x / width) + (0.1f * y / height));
                var defect = x % 127 is > 60 and < 66 && y % 97 is > 40 and < 45;
                pixels.Array[(y * width) + x] = background - (defect ? maximum * 0.08f : 0);
            }
        }
        image.WriteFloat(pixels.Array.AsSpan(0, pixels.Length));
        return image;
    }

    public static Recipe Recipe(ImageDescriptor descriptor, bool threshold = true)
    {
        using var values = new PooledBuffer<float>(checked((int)descriptor.PixelCount));
        values.Array.AsSpan(0, values.Length).Fill(1.02f);
        var flat = new FlatField(new(descriptor, values.Array.AsSpan(0, values.Length), 1), FlatFieldKind.ReciprocalGain);
        FilterSpec[] filters = [flat, new Gaussian(2, 1.1), new Morphology(MorphologyKind.BlackHat, 3)];
        return threshold ? new(1, [.. filters, new Threshold(descriptor.Format == PixelFormat.Gray8 ? 2 : 500)]) : new(1, filters);
    }

    public static ulong Checksum(HostImage image)
    {
        ulong checksum = 14695981039346656037;
        foreach (var value in image.Data.Span)
        {
            checksum = unchecked((checksum ^ value) * 1099511628211);
        }
        foreach (var value in image.Validity.Span)
        {
            checksum = unchecked((checksum ^ value) * 1099511628211);
        }
        return checksum;
    }
}
