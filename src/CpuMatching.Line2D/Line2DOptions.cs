namespace CpuMatching.Line2D;

/// <summary>방향 특징 개수. 기본값은 기존 검출 조건과 같다.</summary>
public sealed record Line2DOptions
{
    public int CoarseFeatures { get; init; } = 64;
    public int FineFeatures { get; init; } = 128;

    public void Validate()
    {
        if (CoarseFeatures is < 16 or > 512 || FineFeatures is < 16 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(Line2DOptions), "Line2D 특징 수는 각각 16~512입니다.");
        }
    }
}
