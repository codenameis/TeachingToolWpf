namespace CpuMatching.DirectionalChamfer;

/// <summary>거리 단위와 특징 개수. 원본 양방향 검증은 항상 유지한다.</summary>
public sealed record DirectionalChamferOptions
{
    public int CoarseFeatures { get; init; } = 64;
    public int FineFeatures { get; init; } = 128;
    public double CoarseTolerance { get; init; } = 5;
    public double FineTolerance { get; init; } = 1;
    public double DirectionPenalty { get; init; } = 1.5;

    public void Validate()
    {
        if (CoarseFeatures is < 16 or > 512 || FineFeatures is < 16 or > 512 ||
            !double.IsFinite(CoarseTolerance) || CoarseTolerance is < .6 or > 8 ||
            !double.IsFinite(FineTolerance) || FineTolerance is < .6 or > 4 ||
            !double.IsFinite(DirectionPenalty) || DirectionPenalty is < .1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(DirectionalChamferOptions),
                "Chamfer 특징 수 16~512, 축소 거리 0.6~8 px, 원본 거리 상한 0.6~4 px, 방향 비용 0.1~8입니다.");
        }
    }
}
