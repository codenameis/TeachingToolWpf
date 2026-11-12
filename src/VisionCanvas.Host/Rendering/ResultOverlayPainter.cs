using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Rendering.Abstractions;
using VisionGeometry;

namespace VisionCanvas.Rendering;

internal static class ResultOverlayPainter
{
    public static void Draw(SpriteMesh mesh, RenderScene scene, IRenderTexture white, Func<string, IRenderTexture> label,
        Func<string, IRenderTexture?> deferredLabel, Func<string, GpuTextRegion?>? batchLabel = null)
    {
        if (scene.Inspection is not { } result)
        {
            return;
        }
        DrawResult(mesh, scene, result, white, label, deferredLabel, batchLabel);
    }

    internal static void DrawResult(SpriteMesh mesh, RenderScene scene, ResultOverlay result, IRenderTexture white,
        Func<string, IRenderTexture> label, Func<string, IRenderTexture?> deferredLabel, Func<string, GpuTextRegion?>? batchLabel,
        List<OverlayStrokeSegment>? strokeScratch = null)
    {
        var strokes = strokeScratch ?? [];
        var view = scene.Document.Viewport;
        var clip = new BoundsD(-3, -3, scene.Width + 3, scene.Height + 3);
        // 모든 라벨 배경을 먼저 그려 인접한 검출 윤곽을 덮어 지우지 않는다.
        foreach (var match in result.Matches)
        {
            var center = view.ImageToScreen(match.ReferencePoint);
            if (center.X >= 0 && center.X <= scene.Width && center.Y >= 0 && center.Y <= scene.Height)
            {
                var text = result.BatchLabels ? batchLabel?.Invoke(match.Label) : null;
                if (text is null && deferredLabel(match.Label) is { } texture)
                {
                    text = Whole(texture);
                }
                if (text is not null)
                {
                    DrawLabel(mesh, scene, white, text, LabelOrigin(scene, match, text), match.Color);
                }
            }
        }
        if (!result.SearchBoundary.IsDefaultOrEmpty)
        {
            DrawContourStroke(mesh, white, result.SearchBoundary, view, clip, strokes, 1.5, result.Color, 8, 5);
        }
        foreach (var match in result.Matches)
        {
            var boundsColor = match.BoundsColor ?? match.Color;
            DrawContourStroke(
                mesh,
                white,
                match.Bounds,
                view,
                clip,
                strokes,
                match.BoundsThickness,
                boundsColor,
                match.BoundsDashLength,
                match.BoundsGapLength);

            var innerColor = match.InnerContourColor ?? match.Color;
            for (var i = 0; i < match.InnerContours.Length; i++)
            {
                DrawContourStroke(
                    mesh,
                    white,
                    match.InnerContours[i],
                    view,
                    clip,
                    strokes,
                    match.InnerContourThickness,
                    innerColor,
                    match.InnerContourDashLength,
                    match.InnerContourGapLength);
            }
            for (var i = 0; i < match.AdditionalContours.Length; i++)
            {
                DrawContourStroke(mesh, white, match.AdditionalContours[i], view, clip, strokes, 2, match.Color);
            }
            if (result.ShowFeatures)
            {
                DrawPoints(mesh, white, view, scene.Width, scene.Height, match.Features, match.Color, 1.5);
                if (!match.DiagnosticGroups.IsDefaultOrEmpty)
                {
                    for (var g = 0; g < match.DiagnosticGroups.Length; g++)
                    {
                        var group = match.DiagnosticGroups[g];
                        DrawPoints(mesh, white, view, scene.Width, scene.Height, group.Points, group.Color, group.HalfSize);
                    }
                }
                else
                {
                    if (!match.KeyFeatures.IsDefaultOrEmpty)
                    {
                        DrawPoints(mesh, white, view, scene.Width, scene.Height, match.KeyFeatures, MatchMarker.KeyFeatureColor, 2.2);
                    }
                    if (!match.UnmatchedFeatures.IsDefaultOrEmpty)
                    {
                        DrawPoints(mesh, white, view, scene.Width, scene.Height, match.UnmatchedFeatures, MatchMarker.UnmatchedFeatureColor, 2.2);
                    }
                }
            }
            var center = view.ImageToScreen(match.ReferencePoint);
            var arm = RenderingConstants.ResultMarkerCrossArmLength;
            mesh.Line(white, center - new PointD(arm, 0), center + new PointD(arm, 0), 2, match.Color);
            mesh.Line(white, center - new PointD(0, arm), center + new PointD(0, arm), 2, match.Color);
            var direction = new PointD(Math.Cos(match.AngleRadians), Math.Sin(match.AngleRadians));
            var end = center + (direction * RenderingConstants.ResultMarkerArrowLength);
            var normal = new PointD(-direction.Y, direction.X);
            mesh.Line(white, center, end, 2, match.Color);
            var headLen = RenderingConstants.ResultMarkerArrowHeadLength;
            var headWid = RenderingConstants.ResultMarkerArrowHeadWidth;
            mesh.Line(white, end, end - (direction * headLen) + (normal * headWid), 2, match.Color);
            mesh.Line(white, end, end - (direction * headLen) - (normal * headWid), 2, match.Color);
        }
        // 미검출·취소 또는 화면 밖 결과도 현재 판정을 확인할 수 있다.
        DrawLabel(mesh, scene, white, Whole(label(result.Summary)), new(14, 12), result.Color);
    }

    private static void DrawPoints(SpriteMesh mesh, IRenderTexture white, ViewportTransform view, double width, double height,
        ImmutableArray<PointD> points, Vector4 color, double halfSize)
    {
        if (points.IsDefaultOrEmpty)
        {
            return;
        }
        var margin = halfSize + 1.0;
        for (var i = 0; i < points.Length; i++)
        {
            var point = view.ImageToScreen(points[i]);
            if (point.X >= -margin && point.X <= width + margin && point.Y >= -margin && point.Y <= height + margin)
            {
                mesh.Rectangle(white, new(point.X - halfSize, point.Y - halfSize, point.X + halfSize, point.Y + halfSize), color);
            }
        }
    }

    private static void DrawContourStroke(SpriteMesh mesh, IRenderTexture white, ImmutableArray<PointD> contour,
        ViewportTransform view, BoundsD clip, List<OverlayStrokeSegment> strokeScratch, double thickness, Vector4 color,
        double dashLength = 0, double gapLength = 0)
    {
        if (contour.IsDefaultOrEmpty)
        {
            return;
        }

        var count = contour.Length;
        Span<PointD> screenPoints = count <= 128 ? stackalloc PointD[count] : new PointD[count];
        for (var i = 0; i < count; i++)
        {
            screenPoints[i] = view.ImageToScreen(contour[i]);
        }

        strokeScratch.Clear();
        OverlayStrokeGeometry.AppendClosedOutline(screenPoints, clip, strokeScratch, dashLength, gapLength);
        DrawStroke(mesh, white, CollectionsMarshal.AsSpan(strokeScratch), thickness, color);
    }

    private static void DrawStroke(SpriteMesh mesh, IRenderTexture white, ReadOnlySpan<OverlayStrokeSegment> strokes, double thickness, Vector4 color)
    {
        var shadowAlpha = Math.Clamp(color.W * 0.85f, 0.25f, 0.9f);
        var shadowThickness = thickness <= 1.25 ? thickness + 1.0 : thickness + 2.0;
        for (var i = 0; i < strokes.Length; i++)
        {
            var segment = strokes[i];
            mesh.Line(white, segment.Start, segment.End, shadowThickness, new(.02f, .02f, .02f, shadowAlpha));
        }
        for (var i = 0; i < strokes.Length; i++)
        {
            var segment = strokes[i];
            mesh.Line(white, segment.Start, segment.End, thickness, color);
        }
    }

    private static GpuTextRegion Whole(IRenderTexture texture) => new(texture, new(0, 0, texture.Width, texture.Height));

    private static PointD LabelOrigin(RenderScene scene, MatchMarker match, GpuTextRegion text)
    {
        var view = scene.Document.Viewport;
        double minX;
        double minY;
        double maxX;
        double maxY;
        if (match.Bounds.IsDefaultOrEmpty)
        {
            var reference = view.ImageToScreen(match.ReferencePoint);
            minX = maxX = reference.X;
            minY = maxY = reference.Y;
        }
        else
        {
            minX = double.PositiveInfinity;
            minY = double.PositiveInfinity;
            maxX = double.NegativeInfinity;
            maxY = double.NegativeInfinity;
            for (var i = 0; i < match.Bounds.Length; i++)
            {
                var p = view.ImageToScreen(match.Bounds[i]);
                if (p.X < minX)
                {
                    minX = p.X;
                }
                if (p.Y < minY)
                {
                    minY = p.Y;
                }
                if (p.X > maxX)
                {
                    maxX = p.X;
                }
                if (p.Y > maxY)
                {
                    maxY = p.Y;
                }
            }

            if (!match.AdditionalContours.IsDefaultOrEmpty)
            {
                for (var c = 0; c < match.AdditionalContours.Length; c++)
                {
                    var contour = match.AdditionalContours[c];
                    for (var i = 0; i < contour.Length; i++)
                    {
                        var p = view.ImageToScreen(contour[i]);
                        if (p.X < minX)
                        {
                            minX = p.X;
                        }
                        if (p.Y < minY)
                        {
                            minY = p.Y;
                        }
                        if (p.X > maxX)
                        {
                            maxX = p.X;
                        }
                        if (p.Y > maxY)
                        {
                            maxY = p.Y;
                        }
                    }
                }
            }
        }

        var width = text.Width / scene.Dpi;
        var height = text.Height / scene.Dpi;
        var x = Math.Max(8, Math.Min(minX + 6, scene.Width - width - 8));
        var y = minY - height - 10;
        if (y < 48)
        {
            y = maxY + 10;
        }
        y = Math.Max(48, Math.Min(y, scene.Height - height - 8));
        return new(x, y);
    }

    private static void DrawLabel(SpriteMesh mesh, RenderScene scene, IRenderTexture white, GpuTextRegion text, PointD origin, Vector4 color)
    {
        var width = text.Width / scene.Dpi;
        var height = text.Height / scene.Dpi;
        var padH = RenderingConstants.ResultLabelPaddingHorizontal;
        var padV = RenderingConstants.ResultLabelPaddingVertical;
        mesh.Rectangle(white, new(origin.X - padH, origin.Y - padV, origin.X + width + padH, origin.Y + height + padV), new(.05f, .07f, .09f, .96f));
        mesh.Rectangle(white, new(origin.X - padH, origin.Y - padV, origin.X - (padH / 2), origin.Y + height + padV), color);
        mesh.Quad(text.Texture, origin, new(origin.X + width, origin.Y),
            new(origin.X + width, origin.Y + height), new(origin.X, origin.Y + height), color, uv: text.Uv);
    }
}
