using CpuMatching.Core;
using CpuMatching.DirectionalChamfer;
using CpuMatching.Line2D;
using CpuMatching.SubNcc;

namespace CpuMatching.Tests;

public sealed class FullSearchAccuracyTests
{
    [Theory]
    [InlineData("Line2D")]
    [InlineData("Chamfer")]
    public void SerialMapPreparationKeepsParallelResults(string method)
    {
        var template = Read("pin-template-106x129.gray", 106, 129);
        ITemplateMatcher matcher = method == "Line2D" ? new Line2DMatcher(template) : new DirectionalChamferMatcher(template);
        var image = Read("pin-crossing-256x256.gray", 256, 256);
        var options = new SearchOptions { MinimumAngle = -30, MaximumAngle = 30, SuppressionRadius = 63.6 };
        var parallel = matcher.Find(image, options);
        var serial = matcher.Find(image, options with
        {
            MaximumDegreeOfParallelism = 1
        });
        Assert.Equal(parallel.Matches, serial.Matches);
        Assert.Equal(parallel.Evaluations, serial.Evaluations);
        Assert.Equal(parallel.CandidateCount, serial.CandidateCount);
        Assert.Equal(parallel.CandidateBatches, serial.CandidateBatches);
    }

    [Theory]
    [InlineData("pin-crossing-256x256.gray")]
    [InlineData("pin-lower-256x256.gray")]
    [InlineData("pin-upper-256x256.gray")]
    public void SerialTileExecutionKeepsParallelScoresAndAllCandidates(string file)
    {
        var matcher = new SubNccMatcher(Read("pin-template-106x129.gray", 106, 129));
        var image = Read(file, 256, 256);
        var options = new SearchOptions { MinimumAngle = -30, MaximumAngle = 30, SuppressionRadius = 63.6 };
        var parallel = matcher.Find(image, options);
        var serial = matcher.Find(image, options with
        {
            MaximumDegreeOfParallelism = 1
        });
        Assert.Equal(parallel.Matches, serial.Matches);
        Assert.Equal(parallel.Evaluations, serial.Evaluations);
        Assert.Equal(parallel.CandidateCount, serial.CandidateCount);
        Assert.Equal(parallel.CandidateBatches, serial.CandidateBatches);
        Assert.False(serial.CandidatesTruncated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void WorkerCountMustBeBounded(int workers)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchOptions { MaximumDegreeOfParallelism = workers }.Validate());
    }

    [Fact]
    public void RepeatedLocationsDoNotEvictADifferentReusableMap()
    {
        const int Width = 40 * 256;
        var pixels = new byte[Width * 1024];
        for (var y = 0; y < 1024; y++)
        {
            pixels.AsSpan((y * Width) + (3 * 256), 34 * 256).Fill(1);
        }
        var prepared = 0;
        var result = PyramidSearch.Find(new(32, 32, new byte[1024]), new(Width, 1024, pixels),
            new SearchOptions { MinimumAngle = 0, MaximumAngle = 0, CandidateLimit = 16 },
            (_, scene, _, _, fine) =>
            {
                if (fine)
                {
                    prepared++;
                }
                return new RepeatedMapPeaks(fine, .9 + (scene[0, 0] * .01));
            }, default, reuseIdenticalTiles: true);
        Assert.Equal(49, result.CandidateCount);
        Assert.Equal(41, result.Matches.Count);
        Assert.Equal(2, prepared);
        Assert.False(result.CandidatesTruncated);
    }

    private sealed class RepeatedMapPeaks(bool fine, double score) : IScoreLevel, IPoseScore
    {
        public bool SupportsParallelEvaluation => true;
        public IPoseScore AtPose(double angleDegrees, double scale) => this;
        public double Score(double x, double y)
        {
            if (fine)
            {
                return score;
            }
            var cell = (x - 64) / 128;
            var firstMap = x is >= 144 and <= 174 && x % 2 == 0;
            var otherMaps = cell == Math.Floor(cell) && (cell is >= 4 and <= 35 || cell == 38);
            return y == 200 && (firstMap || otherMaps) ? 1 : 0;
        }
    }

    [Fact]
    public void CandidateBatchSizeDoesNotDiscardWeakerValidTargets()
    {
        var image = new GrayImage(420, 32, new byte[420 * 32]);
        var template = new GrayImage(8, 8, new byte[64]);
        SearchResult Find(int batchSize) => PyramidSearch.Find(template, image,
            new SearchOptions { MinimumAngle = 0, MaximumAngle = 0, CandidateLimit = batchSize },
            (_, _, _, _, _) => new TwentyPeaks(), default);

        var small = Find(16);
        var large = Find(32);
        Assert.Equal(20, small.Matches.Count);
        Assert.False(small.CandidatesTruncated);
        Assert.Equal(large.Matches, small.Matches);
        Assert.Equal(20, small.CandidateCount);
        Assert.Equal(2, small.CandidateBatches);
        Assert.Equal(1, large.CandidateBatches);
    }

    private sealed class TwentyPeaks : IScoreLevel, IPoseScore
    {
        public bool SupportsParallelEvaluation => true;
        public IPoseScore AtPose(double angleDegrees, double scale) => this;
        public double Score(double x, double y)
            => y == 12 && x >= 10 && x <= 390 && (x - 10) % 20 == 0 ? .95 - (x / 10_000) : 0;
    }

    [Fact]
    public void CancellationDuringABatchNeverReturnsAPartialSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => PyramidSearch.Find(
            new(8, 8, new byte[64]), new(420, 32, new byte[420 * 32]),
            new SearchOptions { MinimumAngle = 0, MaximumAngle = 0, CandidateLimit = 16 },
            (_, _, _, _, _) => new TwentyPeaks(), cancellation.Token, acceptCenter: (_, _) =>
            {
                cancellation.Cancel();
                return true;
            }));
    }

    [Theory]
    [InlineData("pin-crossing-256x256.gray", 135.018, 129.678)]
    [InlineData("pin-lower-256x256.gray", 131.018, 127.678)]
    [InlineData("pin-upper-256x256.gray", 131.018, 129.678)]
    public void FullTemplateRefinementKeepsTheCorrectPeakBesideSimilarCoarsePeaks(string file, double x, double y)
    {
        // 기존 8K 샘플의 원본 Gray8 타일에서 추출했다. 정답은 생성기의 핀 위치이며 검색 결과가 아니다.
        var template = Read("pin-template-106x129.gray", 106, 129);
        var scene = Read(file, 256, 256);
        var result = new SubNccMatcher(template).Find(scene, new SearchOptions
        {
            MinimumAngle = -30,
            MaximumAngle = 30,
            MinimumScore = .75,
            SuppressionRadius = 63.6
        });
        var match = Assert.Single(result.Matches);
        Assert.InRange(Math.Sqrt(Math.Pow(match.X - x, 2) + Math.Pow(match.Y - y, 2)), 0, 1);
        Assert.InRange(Math.Abs(match.AngleDegrees), 0, 1);
        Assert.False(result.CandidatesTruncated);
    }

    private static GrayImage Read(string file, int width, int height)
        => new(width, height, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", file)));
}
