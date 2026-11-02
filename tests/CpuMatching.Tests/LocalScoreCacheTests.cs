using CpuMatching.Core;

namespace CpuMatching.Tests;

public sealed class LocalScoreCacheTests
{
    [Fact]
    public void EvictingOneContentMapPreservesOtherCachedLocationsAndRemovesItsAliases()
    {
        var reads = 0;
        var builds = 0;
        var source = new SearchImage(64 * 256, 1024, region =>
        {
            reads++;
            var pixels = new byte[region.Width * region.Height];
            pixels.AsSpan().Fill((byte)((region.X + 36) / 256 % 40));
            return new(region.Width, region.Height, pixels);
        });
        var level = new LocalScoreLevel(source, 36, pixels =>
        {
            builds++;
            return new ConstantLevel(pixels[0, 0]);
        }, default, reuseIdenticalTiles: true);
        var pose = level.AtPose(0, 1);
        for (var cell = 1; cell <= 32; cell++)
        {
            Assert.Equal(cell, pose.Score((cell * 256) + 128, 384));
        }
        Assert.Equal(32, builds);
        // 다른 위치의 동일 픽셀은 같은 맵을 공유한다.
        Assert.Equal(1, pose.Score((41 * 256) + 128, 384));
        Assert.Equal(32, builds);
        Assert.Equal(33, pose.Score((33 * 256) + 128, 384));
        Assert.Equal(33, builds);
        var previousReads = reads;
        Assert.Equal(32, pose.Score((32 * 256) + 128, 384));
        Assert.Equal(previousReads, reads);
        // 퇴출된 첫 맵의 별칭은 남겨 두지 않는다. 다시 읽고 준비해야 한다.
        Assert.Equal(1, pose.Score((41 * 256) + 128, 384));
        Assert.Equal(previousReads + 1, reads);
        Assert.Equal(34, builds);
    }

    private sealed class ConstantLevel(double score) : IScoreLevel, IPoseScore
    {
        public bool SupportsParallelEvaluation => true;
        public IPoseScore AtPose(double angleDegrees, double scale) => this;
        public double Score(double x, double y) => score;
    }
}
