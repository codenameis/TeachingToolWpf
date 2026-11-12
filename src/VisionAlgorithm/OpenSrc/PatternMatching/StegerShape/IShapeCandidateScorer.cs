using System.Collections.Immutable;

namespace VisionAlgorithm.OpenSrc.PatternMatching;

/// <summary>
/// 양자화 방향 영상의 확산·방향 응답과 여러 템플릿의 정수 점수 격자를 한 번에 계산한다.
/// 후보 선택·예산·연속 방향 검증은 공통 알고리즘이 소유하며 장치 오류를 숨기지 않는다.
/// </summary>
public interface IShapeCandidateScorer
{
    CandidateScoringDiagnostics CandidateDiagnostics
    {
        get;
    }

    /// <summary>
    /// 반환 배열은 호출자가 소유한다. 배열/장치 버퍼를 반환하거나 재사용하기 전에 실행을 완료하며,
    /// 취소도 제출된 작업의 완료를 확인한 뒤 보고한다.
    /// </summary>
    ShapeCandidateScoreBatch Score(ShapeCandidateScoreRequest request, CancellationToken cancellationToken = default);
}
