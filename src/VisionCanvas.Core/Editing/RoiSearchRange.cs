using VisionGeometry;

namespace VisionCanvas.Core.Editing;

/// <summary>ROI 로컬 축 양쪽의 검색 여유(px). 실행 객체와 검사 결과를 보관하지 않는다.</summary>
public sealed record RoiSearchRange
{
    public double Horizontal
    {
        get;
    }

    public double Vertical
    {
        get;
    }

    public RoiSearchRange(double horizontal, double vertical)
    {
        if (!double.IsFinite(horizontal) || horizontal < 0 || !double.IsFinite(vertical) || vertical < 0)
        {
            throw new ArgumentException("검색 여유는 0 이상의 유한한 숫자여야 합니다.");
        }
        Horizontal = horizontal;
        Vertical = vertical;
    }

    public RoiGeometry Expand(RoiGeometry geometry)
        => new(geometry.Center, geometry.Width + (2 * Horizontal), geometry.Height + (2 * Vertical), geometry.Angle);
}
