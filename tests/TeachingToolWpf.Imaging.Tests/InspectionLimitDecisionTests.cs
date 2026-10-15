using System.Collections.Immutable;
using TeachingToolWpf.Imaging.Matching;
using TeachingToolWpf.Models;
using TeachingToolWpf.ViewModels;
using VisionAlgorithm.OpenSrc.PatternMatching;
using VisionGeometry;

namespace TeachingToolWpf.Imaging.Tests;

public sealed class InspectionLimitDecisionTests
{
    [Fact]
    public void ExactlyTheOutputLimitCanPassWhenNoAdditionalMatchWasFound()
    {
        var matches = Enumerable.Range(0, 16)
            .Select(index => new CorrelationMatch(new(index * 100, 50), .95))
            .ToImmutableArray();
        var output = new TemplateSearchOutput(matches, 1, false, 0, 0);

        var decision = InspectionDecision.Evaluate(output);

        Assert.Equal(InspectionVerdict.Pass, decision.Verdict);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void IncompleteOrLimitedOutputCannotClaimPassOrProductFailure(bool truncated, bool matchLimitExceeded, bool found)
    {
        var output = new TemplateSearchOutput(found ? [new(new(50, 50), .95)] : [], 1, truncated, 0, 0, matchLimitExceeded);

        var decision = InspectionDecision.Evaluate(output);

        Assert.Equal(InspectionVerdict.Warning, decision.Verdict);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WarningExplainsCandidateBudgetAndOutputLimitIndependently(bool truncated, bool matchLimitExceeded)
    {
        var output = new TemplateSearchOutput([new(new(50, 50), .95)], 1, truncated, 0, 0, matchLimitExceeded);

        var reason = InspectionDecision.Evaluate(output).Reason;

        Assert.Equal(truncated, reason.Contains("일부 후보를 확인하지 못했습니다", StringComparison.Ordinal));
        Assert.Equal(truncated, reason.Contains("최대 후보 수를 늘리거나 검색 ROI를 줄이세요", StringComparison.Ordinal));
        Assert.Equal(matchLimitExceeded, reason.Contains("추가 검출 결과", StringComparison.Ordinal));
        Assert.Equal(matchLimitExceeded, reason.Contains("최대 결과 수를 늘려", StringComparison.Ordinal));
    }

    [Fact]
    public void NoSearchWindowIsWarningEvenWhenNoPatternWasFound()
    {
        var output = new TemplateSearchOutput([], 0, false, 0, 0);

        Assert.Equal(InspectionVerdict.Warning, InspectionDecision.Evaluate(output).Verdict);
    }

    [Theory]
    [InlineData(false, false, InspectionVerdict.Pass)]
    [InlineData(false, true, InspectionVerdict.Warning)]
    [InlineData(true, false, InspectionVerdict.Warning)]
    [InlineData(true, true, InspectionVerdict.Warning)]
    public void DetectionOutlinesUseTheSameLimitVerdictAsTheResultPanel(bool truncated, bool matchLimitExceeded, InspectionVerdict expected)
    {
        var teachingRegion = new RoiGeometry(new(50, 50), 24, 20, 0);
        var output = new TemplateSearchOutput([new(new(150, 150), .95)], 1, truncated, 0, 0, matchLimitExceeded);

        var marker = Assert.Single(InspectionResultPresentation.Markers(teachingRegion, output));

        Assert.Equal(InspectionResultPresentation.Color(expected), marker.Color);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ShapeWarningsPreserveBothReasonsAndNeverReportProductFailure(bool truncated, bool matchLimitExceeded)
    {
        var output = new ShapeSearchOutput([], 1, truncated, 0, 0, [], matchLimitExceeded);

        var decision = InspectionDecision.Evaluate(output);

        Assert.Equal(InspectionVerdict.Warning, decision.Verdict);
        Assert.Equal(truncated, decision.Reason.Contains("일부 후보를 확인하지 못했습니다", StringComparison.Ordinal));
        Assert.Equal(truncated, decision.Reason.Contains("후보 예산·형상 검증 후보 수를 늘리거나 검색 ROI를 줄이세요", StringComparison.Ordinal));
        Assert.Equal(matchLimitExceeded, decision.Reason.Contains("추가 검출 결과", StringComparison.Ordinal));
    }
}
