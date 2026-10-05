using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class PyramidMatchingIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaperPipelinePreservesRotatedScaledTargetsAndGeometryFallback(bool geometric)
    {
        MatchPose[] expected = [new(100, 100, 0), new(256.3, 256.7, .23, 1.1), new(510.7, 385.2, -.21, .9)];
        var scene = SyntheticScene.Create(641, 517, expected);
        var loader = new Loader(scene);
        var image = new ImageReference("fixture", "fixture", scene.Width, scene.Height);
        var paper = new ShapeMatchingService(loader, () => null, pipeline: ShapeSearchPipeline.Paper);
        var classic = new ShapeMatchingService(loader, () => null);
        var trained = await paper.TrainAsync(image, false, new(new(100, 100), 96, 96, 0), new()
        {
            BuildGeometricModel = geometric,
            BuildClutterModel = true,
            MinimumAngleDegrees = -30,
            MaximumAngleDegrees = 30,
            AngleStepDegrees = 10,
            MinimumScale = .9,
            MaximumScale = 1.1,
            ScaleStep = .1
        }, default);
        var options = new SearchOptions { MaximumParallelism = 4, EvaluateGeometry = geometric, ClutterWeight = 1 };
        var output = await paper.SearchAsync(image, false, trained.Model, null, options, default);
        var phases = Assert.IsType<ShapeSearchPhases>(output.Phases);
        Assert.InRange(phases.SourceOpenMilliseconds + phases.CoarseSearchMilliseconds + phases.OriginalRefinementMilliseconds +
            phases.WindowSearchMilliseconds + phases.FinalSuppressionMilliseconds, 0, output.TotalMilliseconds + 1);
        Assert.False(output.Truncated);
        Assert.False(output.MatchLimitExceeded);
        Assert.Equal(expected.Length, output.Matches.Length);
        foreach (var target in expected)
        {
            var match = Assert.Single(output.Matches.Where(match => Math.Abs(match.Pose.X - target.X) < 1 && Math.Abs(match.Pose.Y - target.Y) < 1));
            Assert.InRange(Math.Abs(match.Pose.Angle - target.Angle), 0, .03);
            Assert.InRange(Math.Abs(match.Pose.Scale - target.Scale), 0, .025);
        }
        if (geometric)
        {
            var unchanged = await classic.SearchAsync(image, false, trained.Model, null, options, default);
            Assert.Equal(unchanged.Matches.ToArray(), output.Matches.ToArray());
            Assert.Equal(unchanged.Windows, output.Windows);
        }
    }

    [Fact]
    public async Task OpenCvAppliesSuppressionOnceAcrossSearchWindowSeam()
    {
        var pattern = new byte[100 * 20];
        var mask = new byte[pattern.Length];
        var scene = new byte[2300 * 20];
        var random = new Random(409);
        for (var y = 0; y < 20; y++)
        {
            for (var x = 38; x < 62; x++)
            {
                var value = random.Next(40, 216);
                pattern[(y * 100) + x] = (byte)value;
                mask[(y * 100) + x] = 255;
                scene[(y * 2300) + 1990 + x] = (byte)Math.Clamp(value + random.Next(-40, 41), 0, 255);
                scene[(y * 2300) + 2040 + x] = (byte)Math.Clamp(value + random.Next(-20, 21), 0, 255);
                scene[(y * 2300) + 2090 + x] = (byte)value;
            }
        }
        var model = CorrelationTemplate.Train(new(pattern, 100, 20, 100), new(mask, new(50, 10)));
        var service = new TemplateMatchingService(new Loader(new(scene, 2300, 20, 2300)), () => null);
        var result = await service.SearchAsync(new("fixture", "fixture", 2300, 20), false, model, null,
            new()
            {
                MinimumScore = .8,
                MaximumOverlap = .3
            }, default);
        Assert.False(result.Truncated);
        Assert.Equal(2, result.Windows);
        Assert.Equal(2, result.Matches.Length);
        Assert.Contains(result.Matches, match => match.ReferencePoint.X == 2040);
        Assert.Contains(result.Matches, match => match.ReferencePoint.X == 2140);
    }

    [Fact]
    public async Task ReducedOriginalTilesMatchTrainingReductionIncludingOddSizesAndStride()
    {
        var bytes = new byte[1060 * 1033];
        new Random(89).NextBytes(bytes);
        var image = new GrayImage(bytes, 1049, 1033, 1060);
        var loader = new Loader(image);
        using (var reader = await MatchingImageReader.OpenAsync(new("fixture", "fixture", 1049, 1033), false, loader, () => null, default))
        {
            var reduced = reader.ReadReduced(0, 0, image.Width, image.Height, 8, default);
            var expected = InspectionPyramid.Reduce(image, 8);
            Assert.Equal(expected.Width, reduced.Width);
            Assert.Equal(expected.Height, reduced.Height);
            Assert.Equal(expected.Pixels.ToArray(), reduced.Pixels.ToArray());
            Assert.Equal(9, loader.Reads);
        }
        Assert.Equal(1, loader.Disposed);
    }

    [Fact]
    public async Task LargeImageCoarseSearchRefinesOriginalPoseAndHonorsSmallCircle()
    {
        var scene = SyntheticScene.Create(2049, 1153, new MatchPose(650, 520, 0, 10), new MatchPose(1517.3, 591.7, 3.4 * Math.PI / 180, 10));
        var loader = new Loader(scene);
        var service = new ShapeMatchingService(loader, () => null);
        var reference = new ImageReference("fixture", "fixture", scene.Width, scene.Height);
        var trained = await service.TrainAsync(reference, false, new(new(650, 520), 512, 512, 0),
            new()
            {
                MinimumAngleDegrees = -10,
                MaximumAngleDegrees = 10,
                AngleStepDegrees = 10
            }, default);
        Assert.Equal(4, trained.Model.Levels.Length);
        var result = await service.SearchAsync(reference, false, trained.Model, null, new(), default);
        Assert.False(result.Truncated);
        Assert.Equal(2, result.Matches.Length);
        Assert.Contains(result.Matches, match => Math.Abs(match.Pose.X - 1517.3) < .5 && Math.Abs(match.Pose.Y - 591.7) < .5 &&
            Math.Abs(match.Pose.Angle - (3.4 * Math.PI / 180)) < .3 * Math.PI / 180);
        var region = RoiGeometry.Create(RoiShapeKind.Circle, new(1517.3, 591.7), 2, 2);
        var limited = await service.SearchAsync(reference, false, trained.Model, region, new(), default);
        Assert.False(limited.Truncated);
        Assert.True(region.Contains(new PointD(Assert.Single(limited.Matches).Pose.X, limited.Matches[0].Pose.Y)));
        Assert.Equal(3, loader.Disposed);
    }

    [Fact]
    public async Task LargeImageMeasuresAndAppliesGeometryLimitsAtOriginalResolution()
    {
        var target = new MatchPose(1517.3, 591.7, 3.4 * Math.PI / 180, 10);
        var scene = SyntheticScene.Create(2049, 1153, new MatchPose(650, 520, 0, 10), target);
        var loader = new Loader(scene);
        var service = new ShapeMatchingService(loader, () => null);
        var reference = new ImageReference("fixture", "fixture", scene.Width, scene.Height);
        var trained = await service.TrainAsync(reference, false, new(new(650, 520), 512, 512, 0),
            new()
            {
                MinimumAngleDegrees = -10,
                MaximumAngleDegrees = 10,
                AngleStepDegrees = 10
            }, default);
        Assert.Equal(4, trained.Model.Levels.Length);
        var region = RoiGeometry.Create(RoiShapeKind.Circle, new(target.X, target.Y), 2, 2);
        var options = new SearchOptions
        {
            MinimumCoverage = .8,
            MaximumFitErrorPixels = 1,
            MaximumClutterRatio = .2
        };
        var result = await service.SearchAsync(reference, false, trained.Model, region, options, default);
        Assert.False(result.Truncated);
        var match = Assert.Single(result.Matches);
        var geometry = Assert.IsType<GeometricMatchQuality>(match.Geometry);
        Assert.Equal(trained.Model.Levels[0].Features.Length, geometry.ModelFeatures);
        Assert.InRange(geometry.Coverage, .8, 1);
        Assert.InRange(geometry.FitErrorPixels, .01, 1);
        Assert.InRange(geometry.ClutterRatio, 0, .2);
        Assert.True(geometry.SceneEdges > geometry.ModelFeatures);
        Assert.InRange(Math.Abs(match.Pose.X - target.X), 0, .5);
        Assert.InRange(Math.Abs(match.Pose.Y - target.Y), 0, .5);

        // 같은 후보를 원본 잔차로 거부한다. 축소 후보의 통과를 최종 합격으로 취급하지 않는다.
        var rejected = await service.SearchAsync(reference, false, trained.Model, region,
            options with
            {
                MaximumFitErrorPixels = .000001
            }, default);
        Assert.False(rejected.Truncated);
        Assert.Empty(rejected.Matches);
        var unevaluated = await service.SearchAsync(reference, false, trained.Model, region,
            options with
            {
                EvaluateGeometry = false
            }, default);
        Assert.False(unevaluated.Truncated);
        Assert.Null(Assert.Single(unevaluated.Matches).Geometry);
        Assert.Equal(4, loader.Disposed);
    }

    private sealed class Loader(GrayImage image) : IImageLoader
    {
        public int Reads;
        public int Disposed;

        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
            => Task.FromResult<ITileSource>(new Source(this, image));
    }

    private sealed class Source(Loader owner, GrayImage image) : ITileSource
    {
        public ImageInfo Info { get; } = new("fixture", "fixture", image.Width, image.Height, PixelKind.Gray8, 1);

        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        {
            Assert.Equal(0, key.Level);
            owner.Reads++;
            var left = key.X * 512;
            var top = key.Y * 512;
            var width = Math.Min(512, image.Width - left);
            var height = Math.Min(512, image.Height - top);
            var pixels = new PixelBuffer(width, height, PixelKind.Gray8);
            for (var y = 0; y < height; y++)
            {
                image.Pixels.Span.Slice(((top + y) * image.Stride) + left, width).CopyTo(pixels.Span.Slice(y * pixels.Stride, width));
            }
            return pixels;
        }

        public void Dispose() => owner.Disposed++;
    }
}
