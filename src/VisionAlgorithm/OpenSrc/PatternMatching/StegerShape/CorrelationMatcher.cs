using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public static class CorrelationMatcher
{
    public static CorrelationResult Search(ShapeGrayImage image, CorrelationTemplate template, ShapeSearchOptions options, CancellationToken token = default)
    {
        var peaks = FindCandidates(image, template, options, token);
        var result = SuppressWithDiagnostics(template, peaks.Matches, options);
        return peaks with
        {
            Matches = result.Matches,
            MatchLimitExceeded = result.MatchLimitExceeded
        };
    }

    /// <summary>창 분할 호출용. 모든 창의 원본 좌표 후보를 모은 후 Suppress를 한 번 적용한다.</summary>
    public static CorrelationResult FindCandidates(ShapeGrayImage image, CorrelationTemplate template, ShapeSearchOptions options, CancellationToken token = default)
        => Imaging.OpenCvTemplateMatcher.Search(image, template, options, token);

    public static ImmutableArray<CorrelationMatch> Suppress(CorrelationTemplate template, IEnumerable<CorrelationMatch> matches, ShapeSearchOptions options)
        => SuppressWithDiagnostics(template, matches, options).Matches;

    /// <summary>출력 상한 뒤의 독립 검출 한 개까지 확인한다. 후보 예산 잘림과 출력 생략을 구별한다.</summary>
    public static CorrelationSuppressionResult SuppressWithDiagnostics(CorrelationTemplate template, IEnumerable<CorrelationMatch> matches, ShapeSearchOptions options)
    {
        options.Validate();
        var selected = new List<CorrelationMatch>();
        foreach (var match in matches.OrderByDescending(value => value.Score).ThenBy(value => value.ReferencePoint.Y).ThenBy(value => value.ReferencePoint.X))
        {
            if (selected.Any(previous => Imaging.OpenCvTemplateMatcher.Overlap(previous.ReferencePoint, match.ReferencePoint, template) > options.MaximumOverlap))
            {
                continue;
            }
            if (selected.Count == options.MaximumMatches)
            {
                return new(selected.ToImmutableArray(), true);
            }
            selected.Add(match);
        }
        return new(selected.ToImmutableArray(), false);
    }
}
