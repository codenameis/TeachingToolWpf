using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

public sealed record ShapeMatch(MatchPose Pose, double CandidateScore, double Score, double Support,
    int SupportedQuadrants, RefinementDiagnostics Refinement)
{
    /// <summary>작은 논문 모델의 원본 gradient 설명 에너지(0~1). null은 세부 검증 미적용.</summary>
    public double? DetailConsistency
    {
        get; init;
    }
    /// <summary>추가 윤곽 감점 전의 학습 특징 방향 일치도.</summary>
    public double DirectionScore { get; init; } = Score;
    /// <summary>최종 자세의 실제 학습 마스크 내부 추가 윤곽 비율. null은 측정하지 않았음을 뜻한다.</summary>
    public double? ClutterRatio
    {
        get; init;
    }
    /// <summary>추가 윤곽 점수에 사용한 대비 에너지 비율. 윤곽 개수 비율과 구별한다.</summary>
    public double? ClutterPenaltyRatio
    {
        get; init;
    }
    /// <summary>레벨 0 특징점별 불일치(누락/반전)한 특징점의 월드 좌표 목록. 시각적 진단 오버레이에 사용한다.</summary>
    public ImmutableArray<ImagePoint> UnmatchedFeatures { get; init; } = [];
    /// <summary>비대칭 구분 키(AsymX, AsymY) 중 정상 일치한 특징점의 월드 좌표 목록.</summary>
    public ImmutableArray<ImagePoint> KeyMatchedFeatures { get; init; } = [];

    public bool Equals(ShapeMatch? other)
        => other is not null &&
           Pose == other.Pose &&
           CandidateScore.Equals(other.CandidateScore) &&
           Score.Equals(other.Score) &&
           Support.Equals(other.Support) &&
           SupportedQuadrants == other.SupportedQuadrants &&
           Refinement == other.Refinement &&
           Nullable.Equals(DetailConsistency, other.DetailConsistency) &&
           DirectionScore.Equals(other.DirectionScore) &&
           Nullable.Equals(ClutterRatio, other.ClutterRatio) &&
           Nullable.Equals(ClutterPenaltyRatio, other.ClutterPenaltyRatio) &&
           (UnmatchedFeatures.IsDefault ? other.UnmatchedFeatures.IsDefaultOrEmpty : (!other.UnmatchedFeatures.IsDefault && UnmatchedFeatures.AsSpan().SequenceEqual(other.UnmatchedFeatures.AsSpan()))) &&
           (KeyMatchedFeatures.IsDefault ? other.KeyMatchedFeatures.IsDefaultOrEmpty : (!other.KeyMatchedFeatures.IsDefault && KeyMatchedFeatures.AsSpan().SequenceEqual(other.KeyMatchedFeatures.AsSpan())));

    public override int GetHashCode()
        => HashCode.Combine(Pose, CandidateScore, Score, Support, SupportedQuadrants, Refinement, DirectionScore);
}
