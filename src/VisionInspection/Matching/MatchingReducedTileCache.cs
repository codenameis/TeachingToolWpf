using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>표시 LOD와 독립인 원본 타일별 면적 평균을 보관한다. 원본 좌표와 축소 배율이 키다.</summary>
internal sealed class MatchingReducedTileCache : MatchingTileCache<(TileKey Key, int Factor)>
{
    public MatchingReducedTileCache() : base(4 * 1024 * 1024)
    {
    }
}
