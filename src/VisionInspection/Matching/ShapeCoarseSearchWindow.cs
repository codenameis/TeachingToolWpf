using VisionAlgorithm;
using VisionAlgorithm.OpenSrc.PatternMatching;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>원본 reader와 분리된 축소 crop의 검색. 창 순서를 유지하고 중첩 CPU 병렬화를 피한다.</summary>
internal sealed record ShapeCoarseSearchWindow(GrayImage Image, int Left, int Top, SearchRegion Region)
{
    public MatchResult Search(ShapeModel model, SearchOptions options, CancellationToken token, ShapeMatcher matcher, int parallelism)
    {
        return matcher.Search(Image, model, options with
        {
            // 축소 점수는 후보 게이트다. 사용자 점수·지지·추가 윤곽 기준은 원본 검증에서 적용한다.
            MinimumScore = options.CandidateThreshold,
            MinimumSupport = 0,
            Refine = false,
            ClutterWeight = 0,
            MaximumVerifiedCandidates = options.MaximumCoarseVerifiedCandidates,
            MaximumMatches = Math.Min(1000, options.MaximumVerifiedCandidates),
            MaximumParallelism = parallelism,
            Region = Region
        }, token);
    }
}
