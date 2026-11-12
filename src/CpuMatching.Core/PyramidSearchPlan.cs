namespace CpuMatching.Core;

/// <summary>피라미드 탐색 시 정밀 에지 보정·중간 레벨 자세 보정·동일 타일 재사용 정책을 묶는다.</summary>
public sealed record PyramidSearchPlan(
    bool RefineEdges = false,
    bool RefineCoarsePositions = false,
    bool ReuseIdenticalTiles = false)
{
    public static PyramidSearchPlan Default { get; } = new();
}
