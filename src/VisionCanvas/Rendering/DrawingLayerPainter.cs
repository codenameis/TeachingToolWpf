using VisionCanvas.Layers;
using VisionGeometry;

namespace VisionCanvas.Rendering;

internal static class DrawingLayerPainter
{
    internal static void Draw(SpriteMesh mesh, RenderScene scene, GpuTexture white, Func<string, GpuTexture?> label,
        Func<string, GpuTexture> immediateLabel, Func<string, GpuTextRegion?>? batchLabel = null)
    {
        foreach (var layer in scene.Layers)
        {
            if (!layer.IsVisible)
            {
                continue;
            }
            foreach (var (_, item) in layer.Items)
            {
                if (item.Graphic is ResultGraphic result)
                {
                    ResultOverlayPainter.DrawResult(mesh, scene, result.Result, white, immediateLabel, label,
                        ReferenceEquals(result.Result, scene.Inspection) ? batchLabel : null);
                }
                else
                {
                    DrawItem(mesh, scene, white, label, layer.Space, item);
                }
            }
        }
    }

    private static PointD Screen(PointD point, LayerSpace space, RenderScene scene)
        => space == LayerSpace.Image ? scene.Document.Viewport.ImageToScreen(point) : point;

    private static void DrawItem(SpriteMesh mesh, RenderScene scene, GpuTexture white, Func<string, GpuTexture?> label,
        LayerSpace space, LayerItem item)
    {
        switch (item.Graphic)
        {
            case ImageCrosshairGraphic axes when scene.Image is { } image:
                var imageCenter = scene.Document.Viewport.ImageToScreen(new(image.Width / 2d, image.Height / 2d));
                Stroke(mesh, scene, white, new(0, imageCenter.Y), new(scene.Width, imageCenter.Y), axes.Thickness, axes.Color);
                Stroke(mesh, scene, white, new(imageCenter.X, 0), new(imageCenter.X, scene.Height), axes.Thickness, axes.Color);
                break;
            case LineGraphic line:
                Stroke(mesh, scene, white, Screen(line.Start, space, scene), Screen(line.End, space, scene), line.Thickness, line.Color);
                break;
            case ShapeGraphic shape:
                for (var i = 0; i < item.Outline.Length; i++)
                {
                    Stroke(mesh, scene, white, Screen(item.Outline[i], space, scene),
                        Screen(item.Outline[(i + 1) % item.Outline.Length], space, scene), shape.Thickness, shape.Color);
                }
                break;
            case TextGraphic text:
                var origin = Screen(text.Position, space, scene);
                if (origin.IsFinite && origin.X >= 0 && origin.Y >= 0 && origin.X < scene.Width && origin.Y < scene.Height &&
                    text.Text.Length > 0 && label(text.Text) is { } texture)
                {
                    mesh.Rectangle(texture, new(origin.X, origin.Y, origin.X + (texture.Width / scene.Dpi), origin.Y + (texture.Height / scene.Dpi)), text.Color);
                }
                break;
            case CrosshairGraphic crosshair:
                var center = crosshair.AtScreenCenter ? new PointD(scene.Width / 2, scene.Height / 2) : Screen(crosshair.Position, space, scene);
                var length = crosshair.ArmLength;
                var gap = crosshair.Gap;
                Stroke(mesh, scene, white, center - new PointD(length, 0), center - new PointD(gap, 0), crosshair.Thickness, crosshair.Color);
                Stroke(mesh, scene, white, center + new PointD(gap, 0), center + new PointD(length, 0), crosshair.Thickness, crosshair.Color);
                Stroke(mesh, scene, white, center - new PointD(0, length), center - new PointD(0, gap), crosshair.Thickness, crosshair.Color);
                Stroke(mesh, scene, white, center + new PointD(0, gap), center + new PointD(0, length), crosshair.Thickness, crosshair.Color);
                break;
        }
    }

    private static void Stroke(SpriteMesh mesh, RenderScene scene, GpuTexture white, PointD start, PointD end, double thickness, System.Numerics.Vector4 color)
    {
        var margin = thickness / 2;
        if (OverlayStrokeGeometry.TryClipLine(start, end, new(-margin, -margin, scene.Width + margin, scene.Height + margin), out var segment))
        {
            mesh.Line(white, segment.Start, segment.End, thickness, color);
        }
    }
}
