namespace VisionGeometry;

/// <summary>원본 이미지 좌표와 뷰포트 DIP 좌표를 변환한다. 물리 픽셀/DPI 변환은 호스트가 담당한다.</summary>
public readonly record struct ViewportTransform(double Scale, PointD Offset)
{
    public static ViewportTransform Identity => new(1, new(0, 0));

    public PointD ImageToScreen(PointD point) => (point * Scale) + Offset;

    public PointD ScreenToImage(PointD point) => (point - Offset) / Scale;

    public ViewportTransform Pan(PointD delta) => this with { Offset = Offset + delta };

    public ViewportTransform ZoomAt(PointD anchor, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }

        var scale = Math.Clamp(Scale * factor, 0.00001, 128);
        // anchor 아래의 원본 좌표가 확대 후에도 같은 화면 위치에 오도록 Offset을 역산한다.
        return new(scale, anchor - (ScreenToImage(anchor) * scale));
    }

    public static ViewportTransform Fit(double imageWidth, double imageHeight, double viewWidth, double viewHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || viewWidth <= 0 || viewHeight <= 0)
        {
            return Identity;
        }

        // 두 축 모두 들어가는 배율에 여백을 두고, 남은 공간의 절반만큼 이동해 중앙에 둔다.
        var scale = Math.Min(viewWidth / imageWidth, viewHeight / imageHeight) * 0.92;
        return new(scale, new(
            (viewWidth - (imageWidth * scale)) / 2,
            (viewHeight - (imageHeight * scale)) / 2));
    }
}
