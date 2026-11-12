using CpuMatching.Core;
using CpuMatching.Line2D;
using CpuMatching.SubNcc;

namespace CpuMatching.Tests;

public sealed class WindowedSearchTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void OriginalRefinementVerifiesConvergedPosesOnceAndKeepsCandidateOrder(int workers)
    {
        var image = new CpuGrayImage(128, 128, new byte[128 * 128]);
        var template = new CpuGrayImage(8, 8, new byte[64]);
        var options = new CpuSearchOptions
        {
            MinimumAngle = -10,
            MaximumAngle = 10,
            PyramidLevels = 1,
            CandidateLimit = 16,
            MaximumDegreeOfParallelism = workers
        };
        var before = new ConvergingScore();
        var after = new ConvergingScore();
        var expectedCenters = new List<(double X, double Y)>();
        var actualCenters = new List<(double X, double Y)>();
        var expected = PyramidSearch.Find(template, image, options, (_, _, _, _, _) => before, default,
            acceptCenter: (x, y) => { expectedCenters.Add((x, y)); return true; });
        var actual = PyramidSearch.FindWindowed(template, new MemorySource(image), options, (_, _, _, _, _) => after, default,
            acceptCenter: (x, y) => { actualCenters.Add((x, y)); return true; });
        Assert.Equal(12, actual.CandidateCount);
        Assert.Equal(4, actual.Matches.Count);
        Assert.Equal(4, before.Verifications);
        Assert.Equal(before.Verifications, after.Verifications);
        Assert.Equal(expected.Matches, actual.Matches);
        Assert.Equal(expected.Evaluations, actual.Evaluations);
        Assert.Equal(expected.CandidateCount, actual.CandidateCount);
        Assert.Equal(expectedCenters, actualCenters);
    }

    [Theory]
    [InlineData(0, 1109, 2)]
    [InlineData(1, 1109, 2)]
    [InlineData(0, 1109, 3)]
    [InlineData(1, 1109, 3)]
    [InlineData(0, 2117, 3)]
    [InlineData(1, 2117, 3)]
    public void GlobalPyramidPhaseBoundaryPeaksAndSuppressionMatchContiguousSearch(int algorithm, int width, int levels)
    {
        const int Height = 1083;
        var pixels = new byte[49 * 53];
        for (var y = 5; y < 48; y++)
        {
            for (var x = 5; x < 44; x++)
            {
                if (x < 14 || y > 36 || (x > 30 && y < 14))
                {
                    pixels[(y * 49) + x] = 225;
                }
            }
        }
        var template = new CpuGrayImage(49, 53, pixels);
        ITemplateMatcher matcher = algorithm switch
        {
            0 => new Line2DMatcher(template),
            _ => new SubNccMatcher(template)
        };
        var scene = new byte[width * Height];
        (int X, int Y)[] locations = [(1012, 1005), (480, 480), (12, 13), (1040, 17)];
        foreach (var (x, y) in locations)
        {
            for (var row = 0; row < 53; row++)
            {
                pixels.AsSpan(row * 49, 49).CopyTo(scene.AsSpan(((y + row) * width) + x, 49));
            }
        }
        var image = new CpuGrayImage(width, Height, scene);
        var source = new MemorySource(image);
        var options = new CpuSearchOptions
        {
            Region = new(7, 9, width - 12, 1069),
            MinimumAngle = -10,
            MaximumAngle = 10,
            PyramidLevels = levels,
            SuppressionRadius = 24,
            CandidateLimit = 16
        };
        var expected = matcher.Find(image, options);
        var actual = matcher.FindWindowed(source, options);
        Assert.Equal(4, actual.Matches.Count);
        Assert.Equal(expected.Matches, actual.Matches);
        Assert.Equal(expected.CandidateCount, actual.CandidateCount);
        Assert.Equal(expected.CandidateBatches, actual.CandidateBatches);
        Assert.False(actual.CandidatesTruncated);
        Assert.False(actual.MatchLimitExceeded);
        Assert.True(source.Reads > 1);
        var limited = matcher.FindWindowed(source, options with
        {
            MaximumMatches = 1
        });
        Assert.Equal(expected.Matches[0], Assert.Single(limited.Matches));
        Assert.True(limited.MatchLimitExceeded);
    }

    [Fact]
    public void ImageLargerThanIntPixelCountIsVisitedWithoutWholeImageAllocation()
    {
        var source = new EmptySource(65_537, 32_769);
        var template = new CpuGrayImage(512, 512, new byte[512 * 512]);
        var result = PyramidSearch.FindWindowed(template, source, new()
        {
            MinimumAngle = 0,
            MaximumAngle = 0,
            PyramidLevels = 5
        }, (_, _, _, _, _) => new ZeroScore(), default);
        Assert.Empty(result.Matches);
        Assert.False(result.CandidatesTruncated);
        Assert.True(source.Right >= source.Width - 15);
        Assert.True(source.Bottom >= source.Height - 15);
        Assert.True(source.MaximumReadPixels <= 2_097_152);
        Assert.True(source.Reads > 100);
    }

    [Fact]
    public void CancellationStopsWindowReading()
    {
        using var cancel = new CancellationTokenSource();
        var source = new EmptySource(17_280, 30_720, cancel);
        var template = new CpuGrayImage(512, 512, new byte[512 * 512]);
        Assert.ThrowsAny<OperationCanceledException>(() => PyramidSearch.FindWindowed(template, source, new()
        {
            MinimumAngle = 0,
            MaximumAngle = 0,
            PyramidLevels = 5
        }, (_, _, _, _, _) => new ZeroScore(), cancel.Token));
        Assert.Equal(3, source.Reads);
    }

    private sealed class MemorySource(CpuGrayImage image) : IGrayImageSource
    {
        public int Width => image.Width;
        public int Height => image.Height;
        public int Reads
        {
            get; private set;
        }
        public CpuGrayImage Read(CpuSearchRegion region, int reduction, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Reads++;
            var result = image.Crop(region);
            for (var factor = 1; factor < reduction; factor *= 2)
            {
                result = result.Half();
            }
            return result;
        }
    }

    private sealed class EmptySource(int width, int height, CancellationTokenSource? cancel = null) : IGrayImageSource
    {
        public int Width => width;
        public int Height => height;
        public int Right
        {
            get; private set;
        }
        public int Bottom
        {
            get; private set;
        }
        public int Reads
        {
            get; private set;
        }
        public int MaximumReadPixels
        {
            get; private set;
        }
        public CpuGrayImage Read(CpuSearchRegion region, int reduction, CancellationToken token)
        {
            Reads++;
            if (Reads == 3)
            {
                cancel?.Cancel();
            }
            token.ThrowIfCancellationRequested();
            Assert.InRange(region.X + region.Width, 4, Width);
            Assert.InRange(region.Y + region.Height, 4, Height);
            Right = Math.Max(Right, region.X + region.Width);
            Bottom = Math.Max(Bottom, region.Y + region.Height);
            var count = region.Width / reduction * (region.Height / reduction);
            MaximumReadPixels = Math.Max(MaximumReadPixels, count);
            return new(region.Width / reduction, region.Height / reduction, new byte[count]);
        }
    }

    private sealed class ZeroScore : IScoreLevel, IPoseScore
    {
        public IPoseScore AtPose(double angleDegrees, double scale) => this;
        public double Score(double x, double y) => 0;
    }

    private sealed class ConvergingScore : IScoreLevel
    {
        private int _verifications;
        public bool SupportsParallelEvaluation => true;
        public int Verifications => _verifications;
        public IPoseScore AtPose(double angleDegrees, double scale) => new PeakScore(angleDegrees);
        public double Verify(Match match)
        {
            Interlocked.Increment(ref _verifications);
            return AtPose(match.AngleDegrees, match.Scale).Score(match.X, match.Y);
        }

        private sealed class PeakScore(double angle) : IPoseScore
        {
            public double Score(double x, double y)
                => y == 60 && x is 20 or 40 or 60 or 80 ? .95 - (Math.Abs(angle) * .001) : 0;
        }
    }
}
