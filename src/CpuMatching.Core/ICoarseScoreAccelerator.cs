namespace CpuMatching.Core;

/// <summary>
/// 한 검색이 독점 대여하는 선택형 가속기. 영상/맵은 대여 동안 불변이며 output 전체를 덮어쓴다.
/// 원본 검증/후보 선택은 CPU가 맡는다. 취소 시 제출 완료 후 반환하고 오류를 숨기지 않는다.
/// </summary>
public interface ICoarseScoreAccelerator : IDisposable
{
    CoarseScoreDiagnostics Diagnostics
    {
        get;
    }

    void ScoreDirections(float[][] maps, DirectionalSample[] points, int width, int height,
        DirectionalSampling sampling, double[] output, CancellationToken token);

    void ScoreCorrelation(CpuGrayImage image, CorrelationSample[] points, double templateEnergy,
        double[] output, CancellationToken token);
}
