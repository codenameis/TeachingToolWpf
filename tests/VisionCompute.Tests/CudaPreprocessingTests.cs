using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;
using VisionCompute.ILGPU;
using Xunit.Abstractions;

namespace VisionCompute.Tests;

public sealed class CudaPreprocessingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(1, 19, 3)]
    [InlineData(27, 1, 5)]
    [InlineData(2, 3, 1)]
    [InlineData(17, 13, 7)]
    [InlineData(513, 259, 11)]
    public void CudaMatchesIndependentConvolutionAndGradientExactly(int width, int height, int padding)
    {
        var image = RandomImage(width, height, padding);
        var original = image.Pixels.ToArray();
        using var preprocessor = new CudaImagePreprocessor();
        var actual = preprocessor.Prepare(image, 3);
        AssertPlanes(ReferenceGradient(image, 3), actual);
        Assert.Equal(original, image.Pixels.ToArray());
        Assert.Equal(1, preprocessor.Diagnostics.Calls);
        Assert.Equal((long)width * height, preprocessor.Diagnostics.ProcessedPixels);
        Assert.Contains("CUDA", preprocessor.Diagnostics.Backend);
        Assert.False(string.IsNullOrWhiteSpace(preprocessor.Diagnostics.AdapterName));
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(preprocessor.Diagnostics));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CpuAndCudaReturnExactlyTheSamePoseScoresAndQuality(bool tiled, bool masked)
    {
        var model = TrainModel(masked);
        var scene = SyntheticScene.Create(253, 197, new MatchPose(102.3, 86.7, .1));
        var options = new SearchOptions { MaximumParallelism = 1, TiledPreprocessing = tiled, ClutterWeight = 1 };
        var expected = Assert.Single(new ShapeMatcher().Search(scene, model, options).Matches);
        using var preprocessor = new CudaImagePreprocessor();
        var actual = new ShapeMatcher(preprocessor).Search(scene, model,
            options with
            {
                ComputeMode = InspectionComputeMode.GpuPreprocessing
            });
        Assert.Equal(expected, Assert.Single(actual.Matches));
        Assert.True(preprocessor.Diagnostics.Calls >= model.Levels.Length);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(preprocessor.Diagnostics));
    }

    [Fact]
    public void PreparedSearchAndDirectRefinementPreserveExactMaskedResults()
    {
        var model = TrainModel(true);
        var scene = SyntheticScene.Create(253, 197, new MatchPose(102.3, 86.7, .1));
        var options = new SearchOptions { MaximumParallelism = 1, ClutterWeight = 1 };
        var cpu = new ShapeMatcher();
        var expectedSearch = Assert.Single(cpu.Search(scene, model, options).Matches);
        var expectedRefinement = Assert.Single(cpu.RefineCandidate(scene, model, new(102, 87, .1), .9, options).Matches);
        using var preprocessor = new CudaImagePreprocessor();
        var gpu = new ShapeMatcher(preprocessor, preprocessor);
        var prepared = PreparedSearchImage.Create(scene, model.Options.MinimumGradient, model.Levels.Length,
            preprocessor: preprocessor);
        var gpuOptions = options with
        {
            ComputeMode = InspectionComputeMode.GpuPreprocessing
        };
        Assert.Equal(expectedSearch, Assert.Single(gpu.Search(prepared, model, gpuOptions).Matches));
        Assert.Equal(expectedRefinement, Assert.Single(gpu.RefineCandidate(scene, model, new(102, 87, .1), .9, gpuOptions).Matches));
        Assert.NotNull(expectedSearch.Geometry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CudaCandidateScoringUsesCpuPreparationAndPreservesExactFinalResults(bool masked)
    {
        var model = TrainModel(masked);
        var scene = SyntheticScene.Create(253, 197, new MatchPose(102.3, 86.7, .1));
        var cpuOptions = new SearchOptions { MaximumParallelism = 1, ClutterWeight = 1 };
        var cpu = new ShapeMatcher();
        var expected = Assert.Single(cpu.Search(scene, model, cpuOptions).Matches);
        var expectedRefinement = Assert.Single(cpu.RefineCandidate(scene, model, new(102, 87, .1), .9, cpuOptions).Matches);
        using var cuda = new CudaImagePreprocessor();
        var gpu = new ShapeMatcher(cuda, cuda);
        var gpuOptions = cpuOptions with
        {
            ComputeMode = InspectionComputeMode.GpuPreprocessing
        };

        var actual = Assert.Single(gpu.Search(scene, model, gpuOptions).Matches);
        Assert.Equal(expected, actual);
        Assert.Equal(0, cuda.Diagnostics.Calls);
        Assert.True(cuda.CandidateDiagnostics.Calls > 0);
        var candidateCalls = cuda.CandidateDiagnostics.Calls;

        var actualRefinement = Assert.Single(gpu.RefineCandidate(scene, model, new(102, 87, .1), .9, gpuOptions).Matches);
        Assert.Equal(expectedRefinement, actualRefinement);
        Assert.Equal(0, cuda.Diagnostics.Calls);
        Assert.Equal(candidateCalls, cuda.CandidateDiagnostics.Calls);
    }

    private static ShapeModel TrainModel(bool masked)
    {
        var training = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var mask = masked ? new byte[96 * 96] : [];
        if (masked)
        {
            for (var y = 0; y < 96; y++)
            {
                for (var x = 0; x < 96; x++)
                {
                    mask[(y * 96) + x] = (byte)((((x - 48) * (x - 48)) + ((y - 48) * (y - 48))) <= 31 * 31 ? 1 : 0);
                }
            }
        }
        return ShapeTrainer.Train(training, new(mask, new(48, 48)),
            new()
            {
                MinimumAngleDegrees = -15,
                MaximumAngleDegrees = 15,
                AngleStepDegrees = 5
            });
    }

    internal static GrayImage RandomImage(int width, int height, int padding)
    {
        var stride = width + padding;
        const int Prefix = 13;
        var length = checked(((height - 1) * stride) + width);
        var bytes = new byte[Prefix + length];
        new Random(1703).NextBytes(bytes);
        return new(bytes.AsMemory(Prefix, length), width, height, stride);
    }

    internal static void AssertPlanes(GradientPlanes expected, GradientPlanes actual)
    {
        Assert.Equal(expected.Magnitude, actual.Magnitude);
        Assert.Equal(expected.Bits, actual.Bits);
        Assert.Equal(expected.X, actual.X);
        Assert.Equal(expected.Y, actual.Y);
    }

    internal static GradientPlanes ReferenceGradient(GrayImage image, float threshold)
    {
        ReadOnlySpan<int> weights = [1, 4, 6, 4, 1];
        var count = image.Width * image.Height;
        var smooth = new float[count];
        var result = new GradientPlanes(new float[count], new float[count], new float[count], new byte[count]);
        var pixels = image.Pixels.Span;
        // 독립 2D 정수 convolution으로 GPU의 중간 float 패스를 재사용하지 않는다.
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var sum = 0;
                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        sum += weights[dy + 2] * weights[dx + 2] * pixels[
                            (Math.Clamp(y + dy, 0, image.Height - 1) * image.Stride) + Math.Clamp(x + dx, 0, image.Width - 1)];
                    }
                }
                smooth[(y * image.Width) + x] = sum / 256f;
            }
        }
        for (var y = 1; y < image.Height - 1; y++)
        {
            for (var x = 1; x < image.Width - 1; x++)
            {
                var i = (y * image.Width) + x;
                var w = image.Width;
                var dx = (smooth[i - w + 1] + (2 * smooth[i + 1]) + smooth[i + w + 1] -
                    smooth[i - w - 1] - (2 * smooth[i - 1]) - smooth[i + w - 1]) / 8;
                var dy = (smooth[i + w - 1] + (2 * smooth[i + w]) + smooth[i + w + 1] -
                    smooth[i - w - 1] - (2 * smooth[i - w]) - smooth[i - w + 1]) / 8;
                var magnitude = MathF.Sqrt((dx * dx) + (dy * dy));
                result.Magnitude[i] = magnitude;
                if (magnitude >= threshold)
                {
                    result.X[i] = dx / magnitude;
                    result.Y[i] = dy / magnitude;
                    var bin = (((int)Math.Round(Math.Atan2(dy, dx) * 8 / Math.PI) % 8) + 8) % 8;
                    result.Bits[i] = (byte)(1 << bin);
                }
            }
        }
        return result;
    }
}
