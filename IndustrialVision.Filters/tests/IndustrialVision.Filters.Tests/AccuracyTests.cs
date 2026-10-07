using IndustrialVision.Filters.Core;
using IndustrialVision.Filters.Gpu;
using IndustrialVision.Filters.Pipeline;

namespace IndustrialVision.Filters.Tests;

public sealed class AccuracyTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var format in Enum.GetValues<PixelFormat>())
        {
            foreach (var size in new[] { (1, 1), (1, 9), (11, 1), (17, 13), (35, 19) })
            {
                foreach (var pattern in new[] { "constant", "impulse", "ramp", "horizontal", "vertical", "checker", "random", "defect", "salt-pepper", "gaussian-noise", "broken-line", "two-regions" })
                {
                    yield return [format, size.Item1, size.Item2, pattern];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ScalarSimdAndParallelMatchIndependentReference(PixelFormat format, int width, int height, string pattern)
    {
        using var input = MakeInput(format, width, height, pattern);
        var values = Read(input.View);
        foreach (var recipe in Recipes(input.Descriptor))
        {
            var expected = ReferenceFilters.Execute(values, width, height, recipe);
            foreach (var options in new[]
            {
                new ExecutionOptions { MaxDegreeOfParallelism = 1, EnableSimd = false },
                new ExecutionOptions { MaxDegreeOfParallelism = 4, EnableSimd = true }
            })
            {
                using var plan = new FilterEngine().Compile(recipe, input.Descriptor, options);
                using var output = new HostImage(plan.Output);
                plan.Execute(input.View, output);
                Compare(expected, output.View, recipe.Filters.All(f => f is Morphology or Threshold));
            }
        }
    }

    [CudaFact]
    public void ActualCudaMatchesReferenceAndPreservesResidentInput()
    {
        using var backend = new CudaBackend();
        foreach (var format in Enum.GetValues<PixelFormat>())
        {
            foreach (var size in new[] { (1, 1), (1, 9), (13, 1), (35, 19) })
            {
                using var input = MakeInput(format, size.Item1, size.Item2, "random");
                foreach (var recipe in Recipes(input.Descriptor))
                {
                    using var plan = new FilterEngine(backend).Compile(recipe, input.Descriptor,
                        new ExecutionOptions { Mode = ExecutionMode.CpuGpu, Fallback = FallbackPolicy.Throw, GpuPlacement = GpuPlacement.PreferGpu });
                    using var output = new HostImage(plan.Output);
                    var expected = ReferenceFilters.Execute(Read(input.View), size.Item1, size.Item2, recipe);
                    var report = plan.Execute(input.View, output);
                    Assert.Equal(input.Descriptor.PixelCount * 4, report.UploadBytes);
                    Assert.Equal(input.Descriptor.PixelCount * 4, report.DownloadBytes);
                    Compare(expected, output.View, recipe.Filters.All(f => f is Morphology or Threshold));
                    plan.ExecuteDevice();
                    plan.CopyOutputToHost(output);
                    Compare(expected, output.View, recipe.Filters.All(f => f is Morphology or Threshold));
                }
            }
        }
    }

    internal static IEnumerable<Recipe> Recipes(ImageDescriptor descriptor)
    {
        var map = new float[checked((int)descriptor.PixelCount)];
        var dark = new float[map.Length];
        for (var i = 0; i < map.Length; i++)
        {
            map[i] = 65000 + (i % 17);
            dark[i] = 10 + (i % 3);
        }
        var flat = new FlatField(new(descriptor, map, 1), dark: new(descriptor, dark, 2), scale: 64000);
        yield return new(1, flat);
        yield return new(1, new FlatField(new(descriptor, map, 1), scale: 64000));
        yield return new(1, new FlatField(new(descriptor, Enumerable.Repeat(0.75f, map.Length).ToArray(), 3), FlatFieldKind.ReciprocalGain));
        yield return new(1, new FlatField(new(descriptor, Enumerable.Repeat(0.75f, map.Length).ToArray(), 3), FlatFieldKind.ReciprocalGain, new(descriptor, dark, 2)));
        yield return new(1, new Gaussian(3, 1.5));
        foreach (var kind in Enum.GetValues<MorphologyKind>())
        {
            yield return new(1, new Morphology(kind, 3));
        }
        yield return new(1, new Threshold(16383));
        yield return new(1, flat, new Gaussian(2, 1.1), new Morphology(MorphologyKind.BlackHat, 2));
        yield return new(1, flat, new Gaussian(2, 1.1), new Morphology(MorphologyKind.BlackHat, 2), new Threshold(100));
    }

    internal static HostImage MakeInput(PixelFormat format, int width, int height, string pattern)
    {
        var bytes = format == PixelFormat.Gray8 ? 1 : format == PixelFormat.Gray16 ? 2 : 4;
        var image = new HostImage(new(width, height, format, (width * bytes) + 7));
        var pixels = new float[width * height];
        var random = new Random(817);
        var maximum = format == PixelFormat.Gray8 ? 255 : 65535;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = pattern switch
                {
                    "constant" => maximum,
                    "impulse" => x == width / 2 && y == height / 2 ? maximum : 0,
                    "ramp" => (x + (y * 3)) % (maximum + 1),
                    "horizontal" => x % (maximum + 1),
                    "vertical" => y % (maximum + 1),
                    "salt-pepper" => random.Next(10) switch { 0 => 0, 1 => maximum, _ => maximum / 2 },
                    "gaussian-noise" => Math.Clamp((maximum / 2f) + ((float)(Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble())) * maximum / 30), 0, maximum),
                    "broken-line" => x == width / 2 && y % 7 != 0 ? maximum : 0,
                    "two-regions" => x % 9 is 2 or 3 or 5 or 6 ? maximum : 0,
                    "checker" => (x + y) % 2 == 0 ? 0 : maximum,
                    "defect" => (maximum / 2) + x - (x == width / 2 ? 10 : 0),
                    _ => random.Next(maximum + 1)
                };
                if (format == PixelFormat.Float32 && pattern == "random")
                {
                    pixels[(y * width) + x] += (float)random.NextDouble() - 1000;
                }
            }
        }
        image.WriteFloat(pixels);
        return image;
    }

    internal static float[] Read(ImageView image)
    {
        var values = new float[checked((int)image.Descriptor.PixelCount)];
        image.CopyToFloat(values);
        return values;
    }

    internal static void Compare(double[] expected, ImageView actual, bool exact = false)
    {
        var values = Read(actual);
        Assert.Equal(expected.Length, values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            if (double.IsNaN(expected[i]))
            {
                Assert.True(float.IsNaN(values[i]), $"Expected invalid pixel {i}.");
            }
            else
            {
                // 실행 전 고정한 수치 기준: 단일/복합 Float32 경로, 모델 근사 없음.
                var tolerance = exact || actual.Descriptor.Format == PixelFormat.Gray8 ? 0 : 0.06 + (0.000001 * Math.Abs(expected[i]));
                Assert.True(Math.Abs(values[i] - expected[i]) <= tolerance,
                    $"Pixel {i}: expected {expected[i]}, actual {values[i]}, tolerance {tolerance}.");
            }
        }
    }
}

public sealed class CudaFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> s_available = new(() => CudaSmoke.Run().Status == "PASS");

    public CudaFactAttribute()
    {
        if (!s_available.Value)
        {
            Skip = "No actual CUDA GPU; CPU accelerator is not a substitute.";
        }
    }
}
