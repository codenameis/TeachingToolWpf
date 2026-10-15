using TeachingToolWpf.Diagnostics;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class TdcmAccuracyTests
{
    [Fact]
    public void MatchingCountsCannotHideMissingDuplicateOrFalsePositive()
    {
        TdcmAccuracy.Pose[] truth = [new(new(10, 10), 0), new(new(100, 100), 0), new(new(200, 200), 0)];
        TdcmAccuracy.Pose[] actual = [new(new(10, 10), 0), new(new(10.5, 10), 0), new(new(300, 300), 0)];
        var report = TdcmAccuracy.Evaluate(truth, actual, 10, true);
        Assert.Equal(2, report.Missing);
        Assert.Equal(1, report.Duplicates);
        Assert.Equal(1, report.FalsePositives);
        Assert.False(report.Passed);
    }

    [Fact]
    public void AngleIsComparedWithFixedTruthAndCannotMoveExpectedCenter()
    {
        TdcmAccuracy.Pose[] truth = [new(new(10, 10), 0)];
        TdcmAccuracy.Pose[] actual = [new(new(12, 10), 180)];
        var report = TdcmAccuracy.Evaluate(truth, actual, 10, true);
        Assert.Equal(1, report.PositionFailures);
        Assert.Equal(1, report.AngleFailures);
        Assert.Equal(180d, report.MaximumAngleError);
        Assert.False(report.Passed);
    }

    [Fact]
    public void UnverifiedLabelsCannotProducePassAndWrappedAnglesAreEquivalent()
    {
        TdcmAccuracy.Pose[] truth = [new(new(10, 10), 0)];
        TdcmAccuracy.Pose[] actual = [new(new(10, 10), 360)];
        Assert.True(TdcmAccuracy.Evaluate(truth, actual, 10, true).Passed);
        Assert.False(TdcmAccuracy.Evaluate(truth, actual, 10, false).Passed);
        Assert.Equal(256, TdcmAccuracy.SamplePins().Length);
    }
}
