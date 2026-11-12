using VisionGeometry;
using VisionImaging.Abstractions;

namespace TeachingToolWpf.Imaging.Matching;

/// <summary>
/// Shared ROI and search-region bounds validation across pattern matching and primitive detection services.
/// </summary>
internal static class MatchingRegionValidator
{
    private const double BoundaryEpsilon = 1e-7;

    public static void ValidateTeachingRegion(ImageReference image, RoiGeometry region)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(region);

        var bounds = region.Bounds;
        if (bounds.Left < -BoundaryEpsilon ||
            bounds.Top < -BoundaryEpsilon ||
            bounds.Right > image.Width + BoundaryEpsilon ||
            bounds.Bottom > image.Height + BoundaryEpsilon)
        {
            throw new ArgumentException("ROI가 원본 이미지 경계를 벗어납니다. 영역을 수정한 뒤 다시 실행하세요.");
        }
    }

    public static void ValidateSearchRegion(ImageReference image, RoiGeometry region)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(region);

        if (!region.Bounds.Intersects(new BoundsD(0, 0, image.Width, image.Height)))
        {
            throw new ArgumentException("검색 범위가 원본 이미지와 겹치지 않습니다.");
        }
    }
}
