namespace VisionGeometry;

/// <summary>Maps image pixels to viewport DIPs. Physical pixel density belongs to the host.</summary>
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
        return new(scale, anchor - (ScreenToImage(anchor) * scale));
    }

    public static ViewportTransform Fit(double imageWidth, double imageHeight, double viewWidth, double viewHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || viewWidth <= 0 || viewHeight <= 0)
        {
            return Identity;
        }

        var scale = Math.Min(viewWidth / imageWidth, viewHeight / imageHeight) * 0.92;
        return new(scale, new((viewWidth - (imageWidth * scale)) / 2, (viewHeight - (imageHeight * scale)) / 2));
    }
}
