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
        var clip = new BoundsD(-3, -3, scene.Width + 3, scene.Height + 3);
        // 모든 라벨 배경을 먼저 그려 인접한 검출 윤곽을 덮어 지우지 않는다.
        foreach (var match in result.Matches)
        {
            var center = view.ImageToScreen(match.ReferencePoint);
            if (center.X >= 0 && center.X <= scene.Width && center.Y >= 0 && center.Y <= scene.Height &&
                deferredLabel(match.Label) is { } text)
            {
                DrawLabel(mesh, scene, white, text, LabelOrigin(scene, match, text), match.Color);
            }
        }
        if (!result.SearchBoundary.IsDefaultOrEmpty)
        {
            DrawStroke(mesh, white, OverlayStrokeGeometry.ClosedOutline(result.SearchBoundary.Select(view.ImageToScreen).ToArray(), clip, 8, 5),
                1.5, result.Color);
        }
        foreach (var match in result.Matches)
        {
            DrawStroke(mesh, white, OverlayStrokeGeometry.ClosedOutline(match.Bounds.Select(view.ImageToScreen).ToArray(), clip), 2, match.Color);
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
        }
        // 미검출·취소 또는 화면 밖 결과도 현재 판정을 확인할 수 있다.
        DrawLabel(mesh, scene, white, label(result.Summary), new(14, 12), result.Color);
    }

    private static void DrawStroke(SpriteMesh mesh, GpuTexture white, IEnumerable<OverlayStrokeSegment> segments, double thickness, Vector4 color)
    {
        var strokes = segments.ToArray();
        foreach (var segment in strokes)
        {
            mesh.Line(white, segment.Start, segment.End, thickness + 2, new(.02f, .02f, .02f, .9f));
        }
        foreach (var segment in strokes)
        {
            mesh.Line(white, segment.Start, segment.End, thickness, color);
        }
    }

    private static PointD LabelOrigin(RenderScene scene, MatchMarker match, GpuTexture text)
    {
        var view = scene.Document.Viewport;
        var bounds = match.Bounds.IsDefaultOrEmpty
            ? BoundsD.FromPoints([view.ImageToScreen(match.ReferencePoint)])
            : BoundsD.FromPoints(match.Bounds.Select(view.ImageToScreen));
        var width = text.Width / scene.Dpi;
        var height = text.Height / scene.Dpi;
        var x = Math.Max(8, Math.Min(bounds.Left + 6, scene.Width - width - 8));
        var y = bounds.Top - height - 10;
        if (y < 48)
        {
            y = bounds.Bottom + 10;
        }
        y = Math.Max(48, Math.Min(y, scene.Height - height - 8));
        return new(x, y);
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
