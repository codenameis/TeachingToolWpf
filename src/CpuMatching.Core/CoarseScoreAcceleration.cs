namespace CpuMatching.Core;

public enum DirectionalSampling
{
    Bilinear,
    RoundedRow
}

/// <summary>템플릿 평균을 뺀 명암 표본. 위치는 템플릿 중심 기준이다.</summary>
public readonly record struct CorrelationSample(double X, double Y, double Value);

public sealed record CoarseScoreDiagnostics(string Device, double InitializationMilliseconds, int Calls,
    long Evaluations, double UploadMilliseconds, double ComputeMilliseconds, double DownloadMilliseconds);

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

    void ScoreCorrelation(GrayImage image, CorrelationSample[] points, double templateEnergy,
        double[] output, CancellationToken token);
}

/// <summary>GPU 라이브러리 참조 없이, 이 자세의 전체 위치 점수를 가속기에 전달한다.</summary>
public interface IAcceleratedPoseScore
{
    void ScoreGrid(ICoarseScoreAccelerator accelerator, double[] output, CancellationToken token);
}
