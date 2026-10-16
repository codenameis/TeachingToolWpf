using CpuMatching.Core;
using CpuMatching.Cuda;
using CpuMatching.DirectionalChamfer;
using CpuMatching.Line2D;
using CpuMatching.SubNcc;

namespace VisionCompute.Tests;

public sealed class CudaTemplateMatchingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CustomAlgorithmParametersRemainEqualOnCpuAndCuda(int algorithm)
    {
        var (template, image) = Scene();
        ITemplateMatcher matcher = algorithm switch
        {
            0 => new Line2DMatcher(template, new() { CoarseFeatures = 80, FineFeatures = 256 }),
            1 => new SubNccMatcher(template, new() { CoarseSamples = 128, MinimumContrast = 16 }),
            _ => new DirectionalChamferMatcher(template, new() { CoarseFeatures = 80, FineFeatures = 256, CoarseTolerance = 4, FineTolerance = 1.2, DirectionPenalty = 2 })
        };
        var options = new SearchOptions { MinimumAngle = -180, MaximumAngle = 180, SuppressionRadius = 16 };
        var cpu = matcher.Find(image, options);
        using var session = new CudaTemplateScoreSession();
        using var accelerator = session.Rent();
        var cuda = matcher.Find(image, options, accelerator: accelerator);
        Assert.Equal(2, cpu.Matches.Count);
        Assert.Equal(cpu.Matches, cuda.Matches);
        Assert.Equal(cpu.Evaluations, cuda.Evaluations);
        Assert.Equal(cpu.CandidatesTruncated, cuda.CandidatesTruncated);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    [InlineData(1, 3)]
    [InlineData(2, 1)]
    [InlineData(2, 3)]
    public void SameCandidatesPoseScoreAndLimitsAtFullAndReducedResolution(int algorithm, int levels)
    {
        var (template, image) = Scene();
        ITemplateMatcher matcher = algorithm switch
        {
            0 => new Line2DMatcher(template),
            1 => new SubNccMatcher(template),
            _ => new DirectionalChamferMatcher(template)
        };
        using var session = new CudaTemplateScoreSession();
        var options = new SearchOptions { MinimumAngle = -180, MaximumAngle = 180, PyramidLevels = levels, SuppressionRadius = 16 };
        Compare(options);
        Compare(options with
        {
            MaximumMatches = 1
        });
        Compare(options with
        {
            CandidateLimit = 16,
            MinimumScore = .6
        });
        Compare(options with
        {
            Region = new(60, 35, 60, 60)
        }, (x, y) => x > 80 && y > 55);

        void Compare(SearchOptions settings, Func<double, double, bool>? acceptCenter = null)
        {
            var cpu = matcher.Find(image, settings, acceptCenter: acceptCenter);
            using var accelerator = session.Rent();
            var gpu = matcher.Find(image, settings, acceptCenter: acceptCenter, accelerator: accelerator);
            Assert.Equal(cpu.Matches, gpu.Matches);
            Assert.Equal(cpu.Evaluations, gpu.Evaluations);
            Assert.Equal(cpu.CandidatesTruncated, gpu.CandidatesTruncated);
            Assert.Equal(cpu.MatchLimitExceeded, gpu.MatchLimitExceeded);
            Assert.True(accelerator.Diagnostics.Calls > 0);
            Assert.Contains("NVIDIA", accelerator.Diagnostics.Device);
        }
    }

    [Fact]
    public void DirectionalGridMatchesCpuIncludingInterpolationAndImageBorders()
    {
        const int Width = 43;
        const int Height = 37;
        var random = new Random(51);
        var maps = Enumerable.Range(0, 16).Select(_ => Enumerable.Range(0, Width * Height).Select(_ => (float)random.NextDouble()).ToArray()).ToArray();
        DirectionalSample[] points = [new(-2.75, 3.25, 15, 0, .37), new(4.5, -1.125, 1, 2, .73)];
        var pose = new DirectionalPoseScore(maps, points, Width, Height);
        var output = new double[Width * Height];
        using var session = new CudaTemplateScoreSession();
        using var accelerator = session.Rent();
        accelerator.ScoreDirections(maps, points, Width, Height, DirectionalSampling.Bilinear, output, default);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                Assert.InRange(Math.Abs(pose.Score(x, y) - output[(y * Width) + x]), 0, 1e-7);
            }
        }
        accelerator.ScoreDirections(maps, [], Width, Height, DirectionalSampling.Bilinear, output, default);
        Assert.All(output, value => Assert.Equal(0, value));
    }

    [Fact]
    public void CancellationExclusiveLeaseDeferredDisposalAndInvalidInputsKeepOwnership()
    {
        var session = new CudaTemplateScoreSession();
        var accelerator = session.Rent();
        var image = new GrayImage(8, 8, Enumerable.Range(0, 64).Select(i => (byte)(i * 3)).ToArray());
        CorrelationSample[] points = [new(-1, 0, -3), new(1, 0, 3)];
        var output = new double[64];
        Assert.Throws<InvalidOperationException>(() => session.Rent());
        Assert.ThrowsAny<OperationCanceledException>(() => accelerator.ScoreCorrelation(image, points, 18, output, new(true)));
        Assert.Throws<ArgumentException>(() => accelerator.ScoreCorrelation(image, points, 18, new double[63], default));
        accelerator.ScoreCorrelation(image, points, 18, output, default);
        Assert.Equal(1, output[27], 12);
        accelerator.Dispose();
        Assert.Throws<ObjectDisposedException>(() => accelerator.ScoreCorrelation(image, points, 18, output, default));
        using var next = session.Rent();
        Assert.Equal(0, next.Diagnostics.InitializationMilliseconds);
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.Rent());
        next.ScoreCorrelation(image, points, 18, output, default);
        Assert.Equal(1, output[27], 12);
        next.Dispose();
        session.Dispose();
    }

    private static (GrayImage Template, GrayImage Image) Scene()
    {
        var pixels = new byte[33 * 29];
        var scene = new byte[128 * 100];
        for (var y = 4; y < 25; y++)
        {
            for (var x = 4; x < 29; x++)
            {
                if (x < 9 || y > 20 || (x > 22 && y < 9))
                {
                    pixels[(y * 33) + x] = 225;
                    scene[((y + 12) * 128) + x + 15] = 225;
                    scene[((x + 48) * 128) + 100 - y] = 225;
                }
            }
        }
        return (new(33, 29, pixels), new(128, 100, scene));
    }
}
