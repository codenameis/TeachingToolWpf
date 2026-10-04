using System.Numerics;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Rendering;

internal static class ResultOverlayPainter
{
    public static void Draw(SpriteMesh mesh, RenderScene scene, GpuTexture white, Func<string, GpuTexture> label,
        Func<string, GpuTexture?> deferredLabel)
    {
        if (scene.Inspection is not { } result)
        {
            return;
        }
        var view = scene.Document.Viewport;
        if (!result.SearchBoundary.IsDefaultOrEmpty)
        {
            mesh.Outline(white, result.SearchBoundary.Select(view.ImageToScreen).ToArray(), 1.5, result.Color);
        }
        foreach (var match in result.Matches)
        {
            mesh.Outline(white, match.Bounds.Select(view.ImageToScreen).ToArray(), 2, match.Color);
            if (result.ShowFeatures)
            {
                foreach (var feature in match.Features)
                {
                    var point = view.ImageToScreen(feature);
                    if (point.X >= -2 && point.X <= scene.Width + 2 && point.Y >= -2 && point.Y <= scene.Height + 2)
                    {
                        mesh.Rectangle(white, new(point.X - 1.5, point.Y - 1.5, point.X + 1.5, point.Y + 1.5), match.Color);
                    }
                }
            }
            var center = view.ImageToScreen(match.ReferencePoint);
            mesh.Line(white, center - new PointD(7, 0), center + new PointD(7, 0), 2, match.Color);
            mesh.Line(white, center - new PointD(0, 7), center + new PointD(0, 7), 2, match.Color);
            var direction = new PointD(Math.Cos(match.AngleRadians), Math.Sin(match.AngleRadians));
            var end = center + (direction * 26);
            var normal = new PointD(-direction.Y, direction.X);
            mesh.Line(white, center, end, 2, match.Color);
            mesh.Line(white, end, end - (direction * 6) + (normal * 4), 2, match.Color);
            mesh.Line(white, end, end - (direction * 6) - (normal * 4), 2, match.Color);
            if (center.X >= 0 && center.X <= scene.Width && center.Y >= 0 && center.Y <= scene.Height)
            {
                if (deferredLabel(match.Label) is { } text)
                {
                    var x = Math.Max(8, Math.Min(center.X + 12, scene.Width - (text.Width / scene.Dpi) - 8));
                    var y = Math.Max(48, Math.Min(center.Y + 12, scene.Height - (text.Height / scene.Dpi) - 8));
                    DrawLabel(mesh, scene, white, text, new(x, y), match.Color);
                }
            }
        }
        // 미검출·취소 또는 화면 밖 결과도 현재 판정을 확인할 수 있다.
        DrawLabel(mesh, scene, white, label(result.Summary), new(14, 12), result.Color);
    }

    private static void DrawLabel(SpriteMesh mesh, RenderScene scene, GpuTexture white, GpuTexture text, PointD origin, Vector4 color)
    {
        var width = text.Width / scene.Dpi;
        var height = text.Height / scene.Dpi;
        mesh.Rectangle(white, new(origin.X - 6, origin.Y - 4, origin.X + width + 6, origin.Y + height + 4), new(.05f, .07f, .09f, .96f));
        mesh.Rectangle(white, new(origin.X - 6, origin.Y - 4, origin.X - 3, origin.Y + height + 4), color);
        mesh.Rectangle(text, new(origin.X, origin.Y, origin.X + width, origin.Y + height), color);
    }
}
