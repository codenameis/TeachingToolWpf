namespace CpuMatching.Core;

public sealed record CpuSearchOptions
{
    public double MinimumAngle { get; init; } = -180;
    public double MaximumAngle { get; init; } = 180;
    public double AngleStep { get; init; } = 10;
    public double MinimumScale { get; init; } = 1;
    public double MaximumScale { get; init; } = 1;
    public double ScaleStep { get; init; } = 0.1;
    public double MinimumScore { get; init; } = 0.75;
    public int MaximumMatches { get; init; } = 64;
    /// <summary>한 번에 정밀 검사할 후보 수. 초과 후보도 다음 묶음에서 모두 검사한다.</summary>
    public int CandidateLimit { get; init; } = 2048;
    public int PyramidLevels { get; init; } = 3;
    /// <summary>각 CPU 단계의 병렬 작업 상한. 여러 영역을 밖에서 병렬 검사할 때는 1로 둔다.</summary>
    public int MaximumDegreeOfParallelism { get; init; } = 4;
    public double SuppressionRadius { get; init; } = 8;
    public CpuSearchRegion? Region
    {
        get; init;
    }

    public void Validate()
    {
        if (!double.IsFinite(MinimumAngle) || !double.IsFinite(MaximumAngle) || MinimumAngle < -360 || MaximumAngle > 360 ||
            MaximumAngle < MinimumAngle || MaximumAngle - MinimumAngle > 360 || !double.IsFinite(AngleStep) || AngleStep is < 0.25 or > 30 ||
            !double.IsFinite(MinimumScale) || !double.IsFinite(MaximumScale) || MinimumScale < 0.5 || MaximumScale > 2 || MaximumScale < MinimumScale ||
            !double.IsFinite(ScaleStep) || ScaleStep is < 0.01 or > 0.5 || !double.IsFinite(MinimumScore) || MinimumScore is <= 0 or > 1 ||
            MaximumMatches is < 1 or > 4096 || CandidateLimit is < 16 or > 16384 || PyramidLevels is < 1 or > 5 ||
            MaximumDegreeOfParallelism is < 1 or > 64 ||
            !double.IsFinite(SuppressionRadius) || SuppressionRadius < 1 ||
            (((MaximumAngle - MinimumAngle) / AngleStep) + 1) * (((MaximumScale - MinimumScale) / ScaleStep) + 1) > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(CpuSearchOptions));
        }
    }
}
