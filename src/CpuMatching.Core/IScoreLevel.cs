namespace CpuMatching.Core;

/// <summary>세 CPU 알고리즘의 공통 피라미드 검색에 쓰이는 레벨별 점수 전략.</summary>
public interface IScoreLevel
{
    /// <summary>AtPose/Verify와 반환된 Score가 읽기 전용일 때만 허용한다. 행 병렬화는 IRowScore에서 별도로 허용한다.</summary>
    bool SupportsParallelEvaluation => false;

    IPoseScore AtPose(double angleDegrees, double scale);

    /// <summary>최종 자세에서 원본 검증 점수를 구한다. 후보 검색 점수와 별도의 검증을 둘 수 있다.</summary>
    double Verify(Match match) => AtPose(match.AngleDegrees, match.Scale).Score(match.X, match.Y);
}
