using CpuMatching.Core;
using CpuMatching.DirectionalChamfer;
using CpuMatching.Line2D;
using CpuMatching.SubNcc;

namespace CpuMatching.Tests;

public sealed class MatchingTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    public void IdenticalTileReusePreservesResultsAndSeparatesChangedPixelsAndBorders(bool changePixel, int expectedPreparations)
    {
        var pixels = new byte[2052 * 1024];
        if (changePixel)
        {
            pixels[(300 * 2052) + 800] = 255;
        }
        var image = new GrayImage(2052, 1024, pixels);
        var baseline = Run(false);
        var shared = Run(true);
        Assert.Equal(4, baseline.Preparations);
        Assert.Equal(expectedPreparations, shared.Preparations);
        Assert.Equal(4, shared.Result.Matches.Count);
        Assert.Equal(baseline.Result.Matches, shared.Result.Matches);
        Assert.Equal(baseline.Result.Evaluations, shared.Result.Evaluations);
        Assert.Equal(baseline.Result.CandidatesTruncated, shared.Result.CandidatesTruncated);
        Assert.Equal(baseline.Result.MatchLimitExceeded, shared.Result.MatchLimitExceeded);

        (SearchResult Result, int Preparations) Run(bool reuse)
        {
            var preparations = 0;
            var result = PyramidSearch.Find(new(32, 32, new byte[1024]), image,
                new SearchOptions { MinimumAngle = 0, MaximumAngle = 0 },
                (_, scene, _, _, fine) =>
                {
                    if (fine)
                    {
                        preparations++;
                    }
                    return new RepeatedLevel(fine, scene.Pixels.Span.Contains((byte)255) ? .95 : .9);
                }, default, reuseIdenticalTiles: reuse);
            return (result, preparations);
        }
    }

    private sealed class RepeatedLevel(bool fine, double value) : IScoreLevel, IPoseScore
    {
        public bool SupportsParallelEvaluation => true;
        public IPoseScore AtPose(double angleDegrees, double scale) => this;
        public double Score(double x, double y)
        {
            if (fine)
            {
                return value;
            }
            return y == 200 && (x == 200 || x == 456 || x == 712 || x == 968) ? 1 : 0;
        }
    }

    [Fact]
    public void ExistingRowScorersKeepTheirSequentialCallingContract()
    {
        var ownerThread = Environment.CurrentManagedThreadId;
        var rows = 0;
        var result = PyramidSearch.Find(new(16, 16, new byte[256]), new(256, 128, new byte[256 * 128]),
            new SearchOptions { MinimumAngle = 0, MaximumAngle = 0, PyramidLevels = 1 },
            (_, _, _, _, _) => new SerialRowLevel(() =>
            {
                Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
                rows++;
            }), default);
        Assert.Equal(128, rows);
        Assert.Empty(result.Matches);
    }

    private sealed class SerialRowLevel(Action checkSerial) : IScoreLevel, IPoseScore, IRowScore
    {
        public bool SupportsParallelEvaluation => true;
        public IPoseScore AtPose(double angleDegrees, double scale) => this;
        public double Score(double x, double y) => 0;
        public void ScoreRow(int y, Span<double> output)
        {
            checkSerial();
            output.Clear();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargeImageLocalMapsKeepNonParallelScoreAndSerialCenterCallbackContracts(bool reuseIdenticalTiles)
    {
        var active = 0;
        var callbackThread = Environment.CurrentManagedThreadId;
        var callbacks = 0;
        var result = PyramidSearch.Find(new(32, 32, new byte[1024]), new(2052, 1024, new byte[2052 * 1024]),
            new SearchOptions { MinimumAngle = 0, MaximumAngle = 0 },
            (_, _, _, _, fine) => new SerialLevel(fine, () =>
            {
                Assert.Equal(0, Interlocked.Exchange(ref active, 1));
                Thread.Yield();
                Volatile.Write(ref active, 0);
            }), default, acceptCenter: (_, _) =>
            {
                Assert.Equal(callbackThread, Environment.CurrentManagedThreadId);
                callbacks++;
                return true;
            }, reuseIdenticalTiles: reuseIdenticalTiles);
        Assert.Equal(4, result.Matches.Count);
        Assert.Equal(4, callbacks);
    }

    private sealed class SerialLevel(bool fine, Action checkSerial) : IScoreLevel, IPoseScore
    {
        public IPoseScore AtPose(double angleDegrees, double scale) => this;
        public double Score(double x, double y)
        {
            if (fine)
            {
                checkSerial();
                return 1;
            }
            return y == 100 && (x == 100 || x == 200 || x == 300 || x == 400) ? 1 : 0;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FindsDistinctRightAngleInstancesAndReportsOutputLimit(int algorithm)
    {
        var pixels = new byte[33 * 29];
        for (var y = 4; y < 25; y++)
        {
            for (var x = 4; x < 29; x++)
            {
                if (x < 9 || y > 20 || (x > 22 && y < 9))
                {
                    pixels[(y * 33) + x] = 225;
                }
            }
        }
        var template = new GrayImage(33, 29, pixels);
        ITemplateMatcher matcher = algorithm switch
        {
            0 => new Line2DMatcher(template),
            1 => new SubNccMatcher(template),
            _ => new DirectionalChamferMatcher(template)
        };
        var buffer = new byte[128 * 100];
        for (var y = 0; y < 29; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                buffer[((y + 12) * 128) + x + 15] = pixels[(y * 33) + x];
                // 독립적인 정수 90도 회전 배치. 보간/검색 구현을 재사용하지 않는다.
                buffer[((x + 48) * 128) + 100 - y] = pixels[(y * 33) + x];
            }
        }
        var image = new GrayImage(128, 100, buffer);
        var options = new SearchOptions { MinimumAngle = -180, MaximumAngle = 180, SuppressionRadius = 16 };
        var result = matcher.Find(image, options);
        Assert.Equal(2, result.Matches.Count);
        Assert.Contains(result.Matches, match => Math.Abs(match.X - 31) < 1 && Math.Abs(match.Y - 26) < 1 && Math.Abs(match.AngleDegrees) < 1);
        Assert.Contains(result.Matches, match => Math.Abs(match.X - 86) < 1 && Math.Abs(match.Y - 64) < 1 && Math.Abs(match.AngleDegrees - 90) < 1);
        var limited = matcher.Find(image, options with
        {
            MaximumMatches = 1
        });
        Assert.Single(limited.Matches);
        Assert.True(limited.MatchLimitExceeded);
        Assert.False(result.CandidatesTruncated);
        Assert.Throws<ArgumentOutOfRangeException>(() => matcher.Find(image, options with { Region = new SearchRegion(120, 0, 20, 20) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => matcher.Find(image, options with { AngleStep = double.NaN }));
    }

    [Fact]
    public void GrayImagePreservesOwnershipAndRejectsInvalidBuffers()
    {
        var pixels = new byte[16];
        var image = new GrayImage(4, 4, pixels);
        pixels[0] = 255;
        Assert.Equal(0, image[0, 0]);
        Assert.Throws<ArgumentException>(() => new GrayImage(4, 4, new byte[15]));
        Assert.Throws<ArgumentException>(() => new Line2DMatcher(image));
        Assert.Throws<ArgumentException>(() => new SubNccMatcher(image));
        Assert.Throws<ArgumentException>(() => new DirectionalChamferMatcher(image));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FindsTranslationAndHonorsRegion(int algorithm)
    {
        var pixels = new byte[32 * 32];
        for (var y = 6; y < 26; y++)
        {
            for (var x = 6; x < 25; x++)
            {
                if (x < 11 || y > 20 || (x > 17 && y < 10))
                {
                    pixels[(y * 32) + x] = 220;
                }
            }
        }
        var template = new GrayImage(32, 32, pixels);
        var scene = new byte[96 * 80];
        for (var y = 0; y < 32; y++)
        {
            pixels.AsSpan(y * 32, 32).CopyTo(scene.AsSpan(((y + 27) * 96) + 39));
        }
        ITemplateMatcher matcher = algorithm switch
        {
            0 => new Line2DMatcher(template),
            1 => new SubNccMatcher(template),
            _ => new DirectionalChamferMatcher(template)
        };
        var result = matcher.Find(new GrayImage(96, 80, scene), new SearchOptions
        {
            MinimumAngle = -15,
            MaximumAngle = 15,
            Region = new SearchRegion(25, 15, 65, 60)
        });
        var match = Assert.Single(result.Matches);
        Assert.InRange(Math.Abs(match.X - 54.5), 0, 1);
        Assert.InRange(Math.Abs(match.Y - 42.5), 0, 1);
        Assert.InRange(Math.Abs(match.AngleDegrees), 0, 1);
        Assert.Empty(matcher.Find(new GrayImage(96, 80, new byte[96 * 80])).Matches);
        Assert.Throws<OperationCanceledException>(() => matcher.Find(new GrayImage(96, 80, scene), cancellationToken: new CancellationToken(true)));
    }
}
