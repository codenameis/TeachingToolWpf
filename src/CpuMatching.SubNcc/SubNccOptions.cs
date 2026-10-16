namespace CpuMatching.SubNcc;

/// <summary>축소 후보의 명암/경계 표본 수. 원본 검증은 내부 픽셀 전체를 쓴다.</summary>
public sealed record SubNccOptions
{
    public int CoarseSamples { get; init; } = 96;
    public int MinimumContrast { get; init; } = 8;

    public void Validate()
    {
        if (CoarseSamples is < 16 or > 512 || CoarseSamples % 2 != 0 || MinimumContrast is < 1 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(SubNccOptions), "Sub-NCC 표본 수는 16~512의 짝수, 최소 명암차는 1~255입니다.");
        }
    }
}
