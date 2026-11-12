using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public sealed record ShapeSearchOptions
{
    public double CandidateThreshold { get; init; } = 0.65;
    public double MinimumScore { get; init; } = 0.72;
    /// <summary>Halcon식 조기 종료 강도(0~1). 0은 수학적 상한(무손실 Branch-and-Bound), 1은 선형 기대 점수 기반 고속 가지치기다.</summary>
    public double Greediness
    {
        get; init;
    }
    /// <summary>부분 가림 허용 비율(0.0 ~ 0.5, 기본값 0.0). 0.0(Strict Mode)은 공간 블록 가림 보정·외곽점 절사·가림 구역 클러터 면제를 모두 끄고 모든 윤곽과 비대칭 문맥을 엄격히 검사하며, 0보다 클 때만 해당 비율만큼 공간 연속 가림 보정을 활성화한다.</summary>
    public double MaxOcclusionRatio
    {
        get; init;
    }
    /// <summary>비대칭 문맥(AsymX, AsymY) 및 대칭 반전 위치(Mirror Counterpart) 역방향 에지 침범을 엄격히 검증할지 여부(기본값 true).</summary>
    public bool StrictContext { get; init; } = true;
    /// <summary>방향 점수 × (1 - 가중치 × 추가 윤곽 에너지 비율).
    /// 작은 논문 모델은 원본 세부 설명 에너지 D의 (1 - 가중치 × (1-D))를 추가 적용한다. 0은 방향 점수를 유지한다.</summary>
    public double ClutterWeight
    {
        get; init;
    }
    public double MinimumSupport { get; init; } = 0.6;
    public int MaximumCandidates { get; init; } = 2048;
    public int MaximumVerifiedCandidates { get; init; } = 64;
    public int MaximumCoarseVerifiedCandidates { get; init; } = 256;
    public int MaximumMatches { get; init; } = 16;
    public double MaximumOverlap { get; init; } = 0.3;
    public bool Refine { get; init; } = true;
    public bool Verify { get; init; } = true;
    /// <summary>최종 원본 검색 영상 픽셀 단위의 에지 대응 허용 거리.</summary>
    public double ClutterTolerancePixels { get; init; } = 3;
    public bool UseSimd { get; init; } = true;
    public bool TiledPreprocessing
    {
        get; init;
    }
    public bool GroupTemplates
    {
        get; init;
    }
    public int MaximumParallelism { get; init; } = 1;
    public InspectionComputeMode ComputeMode { get; init; } = InspectionComputeMode.Cpu;
    public ShapeSearchRegion? Region
    {
        get; init;
    }

    public void Validate()
    {
        foreach (var value in new[] { CandidateThreshold, MinimumScore, MinimumSupport, MaximumOverlap, ClutterWeight, Greediness })
        {
            if (!double.IsFinite(value) || value is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(ShapeSearchOptions));
            }
        }
        if (!double.IsFinite(MaxOcclusionRatio) || MaxOcclusionRatio is < 0 or > 0.5)
        {
            throw new ArgumentOutOfRangeException(nameof(ShapeSearchOptions));
        }
        if (!double.IsFinite(ClutterTolerancePixels) || ClutterTolerancePixels is < 0.5 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(ShapeSearchOptions));
        }
        if (MaximumCandidates is < 1 or > 100000 || MaximumVerifiedCandidates is < 1 or > 4096 || MaximumCoarseVerifiedCandidates is < 1 or > 4096 ||
            MaximumMatches is < 1 or > 1000 || MaximumParallelism is < 1 or > 16 || !Enum.IsDefined(ComputeMode))
        {
            throw new ArgumentOutOfRangeException(nameof(ShapeSearchOptions));
        }
    }
}
