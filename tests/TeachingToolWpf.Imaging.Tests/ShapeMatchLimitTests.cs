using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Presentation;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionAlgorithm.Tests;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class ShapeMatchLimitTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BothSearchPathsReportOmittedMatchesWithoutConfusingAnExactLimit(bool pyramid, bool geometric)
    {
        var width = pyramid ? 2200 : 1536;
        var height = pyramid ? 1100 : 256;
        var first = pyramid ? new PointD(400, 400) : new PointD(80, 90);
        var second = pyramid ? new PointD(1600, 700) : new PointD(769, 140);
        var pattern = SyntheticScene.Create(96, 96, new MatchPose(48, 48, 0));
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)25);
        foreach (var center in new[] { first, second })
        {
            for (var row = 0; row < pattern.Height; row++)
            {
                pattern.Pixels.Span.Slice(row * pattern.Stride, pattern.Width)
                    .CopyTo(pixels.AsSpan((((int)center.Y - 48 + row) * width) + (int)center.X - 48));
            }
        }
        var service = new ShapeMatchingService(new Loader(pixels, width, height), () => null);
        var image = new ImageReference("fixture", "fixture", width, height);
        var teachingRegion = new RoiGeometry(first, 65, 65, 0);
        var training = await service.TrainAsync(image, false, teachingRegion, new()
        {
            MinimumAngleDegrees = 0,
            MaximumAngleDegrees = 0,
            BuildGeometricModel = geometric
        }, default);
        var options = new SearchOptions
        {
            MinimumScore = .8,
            MaximumMatches = 1,
            EvaluateGeometry = geometric
        };

        var limited = await service.SearchAsync(image, false, training.Model, null, options, default);
        Assert.False(limited.Truncated);
        Assert.Single(limited.Matches);
        Assert.True(limited.MatchLimitExceeded);
        Assert.Equal(InspectionVerdict.Warning, InspectionDecision.Evaluate(limited).Verdict);
        Assert.Equal(InspectionResultPresentation.Color(InspectionVerdict.Warning),
            Assert.Single(InspectionResultPresentation.Markers(training.Model, teachingRegion, limited)).Color);

        var complete = await service.SearchAsync(image, false, training.Model, null, options with
        {
            MaximumMatches = 2
        }, default);
        Assert.False(complete.Truncated);
        Assert.False(complete.MatchLimitExceeded);
        Assert.Equal(2, complete.Matches.Length);
        Assert.NotEqual(InspectionVerdict.Warning, InspectionDecision.Evaluate(complete).Verdict);
        Assert.Contains(complete.Matches, match => Math.Abs(match.Pose.X - first.X) < .5 && Math.Abs(match.Pose.Y - first.Y) < .5);
        Assert.Contains(complete.Matches, match => Math.Abs(match.Pose.X - second.X) < .5 && Math.Abs(match.Pose.Y - second.Y) < .5);
    }

    private sealed class Loader(byte[] pixels, int width, int height) : IImageLoader
    {
        public Task<ITileSource> LoadAsync(ImageReference reference, IProgress<string> progress, CancellationToken cancellationToken)
            => Task.FromResult<ITileSource>(new Source(pixels, width, height));
    }

    private sealed class Source(byte[] pixels, int width, int height) : ITileSource
    {
        public ImageInfo Info { get; } = new("fixture", "fixture", width, height, PixelKind.Gray8, 1);

        public PixelBuffer Read(TileKey key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(0, key.Level);
            var left = key.X * 512;
            var top = key.Y * 512;
            var tileWidth = Math.Min(512, width - left);
            var tileHeight = Math.Min(512, height - top);
            var tile = new PixelBuffer(tileWidth, tileHeight, PixelKind.Gray8);
            for (var row = 0; row < tileHeight; row++)
            {
                pixels.AsSpan(((top + row) * width) + left, tileWidth).CopyTo(tile.Span.Slice(row * tile.Stride, tileWidth));
            }
            return tile;
        }

        public void Dispose()
        {
        }
    }
}
