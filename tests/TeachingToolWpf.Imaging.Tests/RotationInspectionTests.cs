using TeachingToolWpf.Imaging.Matching;
using VisionAlgorithm.OpenSrc.PatternMatching;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Imaging;
using TeachingToolWpf.Presentation;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class RotationInspectionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task UserPcbTeachingMarksAllEightReversedPinsNg(double minimumAccepted)
    {
        var editor = new EditorSession();
        editor.InitializeImage(new("sample:2048:2048", "PCB single period", 2048, 2048), ViewportTransform.Identity);
        editor.Add(new(new(760.793, 598.841), 110.787, 245.01, 0));
        var service = new ShapeMatchingService(new ImageLoader(System.IO.Path.GetTempPath()), () => null,
            pipeline: ShapeSearchPipeline.Paper);
        using var viewModel = new ShapeMatchingViewModel(service)
        {
            MinimumAngle = 0, MaximumAngle = 10, InspectRotation = true,
            MinimumAcceptedAngle = minimumAccepted, MaximumAcceptedAngle = 10
        };
        viewModel.Update(editor.State, 1, false, true);
        var unchanged = editor.State;
        await viewModel.Train.ExecuteAsync();
        Assert.False(viewModel.IsTrainingDirty);
        Assert.NotNull(viewModel.Model);
        await viewModel.Search.ExecuteAsync();
        Assert.Equal(InspectionVerdict.Fail, viewModel.Verdict);
        Assert.Equal(16, viewModel.Markers.Length);
        var lower = viewModel.Markers.Where(marker => marker.ReferencePoint.Y > 1000).ToArray();
        Assert.Equal(8, lower.Length);
        Assert.All(lower, marker =>
        {
            Assert.InRange(Math.Abs(marker.AngleRadians * 180 / Math.PI), 179, 181);
            Assert.Contains("NG", marker.Label);
            Assert.Equal(InspectionResultPresentation.Color(InspectionVerdict.Fail), marker.Color);
        });
        Assert.False(viewModel.InspectionLogs[^1].Truncated);
        Assert.False(viewModel.InspectionLogs[^1].MatchLimitExceeded);
        if (minimumAccepted == -10)
        {
            Assert.Equal(8, viewModel.InspectionLogs[^1].RotationFailureCount);
            Assert.All(viewModel.Markers.Where(marker => marker.ReferencePoint.Y < 1000), marker => Assert.Contains("OK", marker.Label));
        }
        Assert.Same(unchanged, editor.State);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(10, 0, true)]
    [InlineData(10.001, 0, false)]
    [InlineData(-.001, 0, false)]
    [InlineData(180, 0, false)]
    [InlineData(-180, 0, false)]
    [InlineData(365, 0, true)]
    [InlineData(5, 355, true)]
    [InlineData(175, 355, false)]
    public void AcceptanceUsesTeachingRelativeAngleAcrossWrap(double angle, double reference, bool accepted)
        => Assert.Equal(accepted, new RotationAcceptance(0, 10).Contains(Radians(angle), Radians(reference)));

    [Fact]
    public void AcceptanceCanCrossOppositeDirectionBoundary()
    {
        var acceptance = new RotationAcceptance(170, 190);
        Assert.True(acceptance.Contains(Radians(-180), 0));
        Assert.True(acceptance.Contains(Radians(-170), 0));
        Assert.False(acceptance.Contains(Radians(-169), 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RotationFailureDoesNotOverrideIncompleteSearch(bool outputLimit)
    {
        var output = new ShapeSearchOutput([Match(180)], 1, !outputLimit, 0, 0, [], outputLimit);
        Assert.Equal(InspectionVerdict.Warning, InspectionDecision.Evaluate(output, new()).Verdict);
    }

    [Fact]
    public void ReversedPartIsNgWithoutChangingScoreAndUnobservableAngleIsCheck()
    {
        var match = Match(180);
        Assert.Equal(InspectionVerdict.Pass, InspectionDecision.Evaluate(match).Verdict);
        Assert.Equal(InspectionVerdict.Fail, InspectionDecision.Evaluate(match, new()).Verdict);
        Assert.Equal(InspectionVerdict.Review, InspectionDecision.Evaluate(match with
        {
            Refinement = match.Refinement with { AngleObservable = false }
        }, new()).Verdict);
        var output = new ShapeSearchOutput([Match(0), match], 1, false, 0, 0, []);
        var decision = InspectionDecision.Evaluate(output, new());
        Assert.Equal(InspectionVerdict.Fail, decision.Verdict);
        Assert.Contains("1개 / 검출 2개", decision.Reason);
        Assert.Equal(.99, match.Score);
    }

    [Theory]
    [InlineData(double.NaN, 10)]
    [InlineData(10, 0)]
    [InlineData(-181, 180)]
    [InlineData(-361, -360)]
    public void InvalidAcceptanceIsRejected(double minimum, double maximum)
        => Assert.Throws<ArgumentException>(() => new RotationAcceptance(minimum, maximum).Validate());

    private static ShapeMatch Match(double angle)
    {
        var pose = new MatchPose(100, 100, Radians(angle));
        return new(pose, 1, .99, 1, 4, new(RefinementStatus.Converged, pose, 1, 0, 2, 100, true));
    }
    private static double Radians(double degrees) => degrees * Math.PI / 180;
}
