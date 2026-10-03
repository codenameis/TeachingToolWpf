namespace TeachingToolWpf.Core.Geometry;

/// <summary>One DIP-space layout for GPU display and pointer navigation.</summary>
public sealed record MinimapLayout(BoundsD Panel, BoundsD ImageBounds, double ImageWidth, double ImageHeight, double ViewWidth, double ViewHeight)
{
    public static MinimapLayout? Create(double imageWidth, double imageHeight, double viewWidth, double viewHeight)
    {
        if (!double.IsFinite(imageWidth + imageHeight + viewWidth + viewHeight) || imageWidth <= 0 || imageHeight <= 0 || viewWidth < 320 || viewHeight < 240)
        {
            return null;
        }

        var width = Math.Min(232, viewWidth * .3);
        var height = Math.Min(174, viewHeight * .35);
        var panel = new BoundsD(viewWidth - width - 12, viewHeight - height - 12, viewWidth - 12, viewHeight - 12);
        var content = new BoundsD(panel.Left + 10, panel.Top + 30, panel.Right - 10, panel.Bottom - 10);
        var scale = Math.Min(content.Width / imageWidth, content.Height / imageHeight);
        var half = new PointD(imageWidth * scale / 2, imageHeight * scale / 2);
        return new(panel, new(content.Center.X - half.X, content.Center.Y - half.Y, content.Center.X + half.X, content.Center.Y + half.Y), imageWidth, imageHeight, viewWidth, viewHeight);
    }

    public PointD ImageToMap(PointD point) => new(ImageBounds.Left + (point.X / ImageWidth * ImageBounds.Width), ImageBounds.Top + (point.Y / ImageHeight * ImageBounds.Height));

    public PointD MapToImage(PointD point) => new(Math.Clamp((point.X - ImageBounds.Left) / ImageBounds.Width, 0, 1) * ImageWidth,
        Math.Clamp((point.Y - ImageBounds.Top) / ImageBounds.Height, 0, 1) * ImageHeight);

    public BoundsD VisibleBounds(ViewportTransform view)
    {
        var a = view.ScreenToImage(new(0, 0));
        var b = view.ScreenToImage(new(ViewWidth, ViewHeight));
        a = ImageToMap(new(Math.Clamp(a.X, 0, ImageWidth), Math.Clamp(a.Y, 0, ImageHeight)));
        b = ImageToMap(new(Math.Clamp(b.X, 0, ImageWidth), Math.Clamp(b.Y, 0, ImageHeight)));
        return new(a.X, a.Y, b.X, b.Y);
    }

    public ViewportTransform CenterView(ViewportTransform view, PointD center)
    {
        static double Limit(double center, double image, double visible) => visible >= image ? image / 2 : Math.Clamp(center, visible / 2, image - (visible / 2));
        var target = new PointD(Limit(center.X, ImageWidth, ViewWidth / view.Scale), Limit(center.Y, ImageHeight, ViewHeight / view.Scale));
        return new(view.Scale, new PointD(ViewWidth / 2, ViewHeight / 2) - (target * view.Scale));
    }
}
