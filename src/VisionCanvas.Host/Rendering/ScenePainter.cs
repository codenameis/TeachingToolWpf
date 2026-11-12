using System.Numerics;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Imaging;
using VisionCanvas.Rendering.Abstractions;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

internal sealed class ScenePainter
{
    private readonly SpriteMesh _mesh = new();
    private readonly RoiLabelPainter _labels = new();
    private readonly List<OverlayStrokeSegment> _strokeScratch = [];
    private static readonly Vector4 s_accent = new(.22f, .52f, 1, 1);

    public int RoiBatchBoundary
    {
        get;
        private set;
    }

    public SpriteMesh Build(RenderScene scene, IRenderTexture white, IReadOnlyDictionary<TileKey, IRenderTexture> tiles, Func<string, IRenderTexture> label,
        Func<string, IRenderTexture?> deferredLabel, IRenderTexture? live, BoundsD liveBounds, GpuFilterPreview filters, IRenderTexture? liveOverview,
        Func<string, GpuTextRegion?>? batchLabel = null)
    {
        _mesh.Begin(scene.Width, scene.Height);
        DrawBackgroundImage(scene, tiles, live, liveBounds);
        DrawFilterRegions(scene.Document.Viewport, filters);
        RoiBatchBoundary = _mesh.Split();
        DrawDragFill(_mesh, scene, white);
        DrawHoverAndSelectionHandles(scene, white);
        DrawDraftOverlays(scene, white);
        DrawSceneLabelsAndMinimap(scene, white, tiles, label, deferredLabel, liveOverview, batchLabel);
        return _mesh;
    }

    private void DrawBackgroundImage(RenderScene scene, IReadOnlyDictionary<TileKey, IRenderTexture> tiles, IRenderTexture? live, BoundsD liveBounds)
    {
        var view = scene.Document.Viewport;
        if (scene.IsLive && live is not null)
        {
            var a = view.ImageToScreen(new(liveBounds.Left, liveBounds.Top));
            var b = view.ImageToScreen(new(liveBounds.Right, liveBounds.Bottom));
            _mesh.Rectangle(live, new(a.X, a.Y, b.X, b.Y), Vector4.One, scene.PointSampling);
            return;
        }
        if (scene.IsLive || scene.Image is not { } image)
        {
            return;
        }

        var visible = TilePlanner.Visible(image, view, scene.Width, scene.Height, scene.Dpi);
        if (tiles.TryGetValue(new(image.Levels - 1, 0, 0), out var overview) && !OpaqueTilesCover(image, visible, tiles))
        {
            var topLeft = view.ImageToScreen(new(0, 0));
            var bottomRight = view.ImageToScreen(new(image.Width, image.Height));
            _mesh.Rectangle(overview, new(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y), Vector4.One, scene.PointSampling);
        }
        foreach (var key in visible)
        {
            if (!tiles.TryGetValue(key, out var texture))
            {
                continue;
            }

            var factor = 1 << key.Level;
            var a = view.ImageToScreen(new(key.X * 512d * factor, key.Y * 512d * factor));
            var b = view.ImageToScreen(new(Math.Min(image.Width, ((key.X * 512d) + texture.Width) * factor), Math.Min(image.Height, ((key.Y * 512d) + texture.Height) * factor)));
            _mesh.Rectangle(texture, new(a.X, a.Y, b.X, b.Y), Vector4.One, scene.PointSampling);
        }
    }

    private void DrawFilterRegions(ViewportTransform view, GpuFilterPreview filters)
    {
        foreach (var (region, texture, uv) in filters.Regions)
        {
            var corners = region.Geometry.FrameCorners.Select(view, static (v, p) => v.ImageToScreen(p));
            _mesh.Quad(texture, corners.A, corners.B, corners.C, corners.D, Vector4.One, true, uv);
        }
    }

    private void DrawHoverAndSelectionHandles(RenderScene scene, IRenderTexture white)
    {
        var view = scene.Document.Viewport;
        if (scene.Overlay.Hover is { } hover && scene.Document.Rois.TryGetValue(hover, out var hovered))
        {
            var style = scene.RoiStyles.For(RoiStyleRoles.Resolve(scene.Document, hover));
            var hoveredGeometry = scene.EffectiveGeometry(hovered);
            DrawGeometryScreenOutline(
                white,
                hoveredGeometry,
                view,
                style.StrokeThickness(true),
                style.StrokeColor(true, scene.Document.SelectionCount > 1 && scene.Document.Selection.Contains(hover)));
        }

        if (scene.Document.SelectionCount == 1 && !scene.MaskEditing)
        {
            var roi = scene.Document.Rois[scene.Document.Selection.Single()];
            var color = scene.RoiStyles.For(RoiStyleRoles.Resolve(scene.Document, roi.Id)).SelectedColor;
            var geometry = scene.EffectiveGeometry(roi);
            var points = geometry.FrameCorners.Select(view, static (v, p) => v.ImageToScreen(p));
            Span<PointD> handles = stackalloc PointD[8];
            HandleGeometry.CopyResizeHandlesTo(geometry, view, handles);
            for (var i = 0; i < handles.Length; i++)
            {
                Handle(white, handles[i], scene.Overlay.HandleScale, color);
            }

            var top = (points.A + points.B) / 2;
            var rotation = HandleGeometry.RotationHandle(geometry, view, scene.Overlay.HandleScale);
            _mesh.Line(white, top, rotation, 1, color);
            RotationHandle(white, rotation, color, scene.Overlay.HandleScale);
        }

        if (scene.Document.SelectionCount > 1)
        {
            var selectionColor = scene.RoiStyles.Default.MultiSelectedColor;
            var bounds = scene.Document.SelectedSpatial.Bounds;
            var transform = scene.Transform?.Transform ?? default;
            var corners = SelectionCorners(bounds, view, transform);
            _mesh.Outline(white, corners, 1, selectionColor);
        }
    }

    private void DrawDraftOverlays(RenderScene scene, IRenderTexture white)
    {
        var view = scene.Document.Viewport;
        var draftStyle = scene.Overlay.DraftIsGroup ? scene.RoiStyles.Parent : scene.RoiStyles.Child;
        if (scene.Overlay.Draft is { } draft)
        {
            DrawGeometryScreenOutline(white, draft, view, draftStyle.StrokeThickness(true), draftStyle.SelectedColor);
        }

        // [Domain Why]: 드래프트 다각형 꼭짓점 핸들의 고정 화면 반경 (5px).
        const double draftHandleRadius = 5.0;
        var polygonPoints = scene.Overlay.PolygonPoints;
        if (!polygonPoints.IsDefaultOrEmpty)
        {
            var previous = view.ImageToScreen(polygonPoints[0]);
            Handle(white, previous, draftHandleRadius, draftStyle.SelectedColor);
            for (var i = 1; i < polygonPoints.Length; i++)
            {
                var current = view.ImageToScreen(polygonPoints[i]);
                _mesh.Line(white, previous, current, draftStyle.StrokeThickness(true), draftStyle.SelectedColor);
                Handle(white, current, draftHandleRadius, draftStyle.SelectedColor);
                previous = current;
            }
        }

        if (scene.Overlay.Marquee is { } selection)
        {
            var marqueeCorners = new QuadPoints(
                new(selection.Left, selection.Top),
                new(selection.Right, selection.Top),
                new(selection.Right, selection.Bottom),
                new(selection.Left, selection.Bottom));
            _mesh.Outline(white, marqueeCorners, 1, s_accent);
        }

        if (scene.Alignment is { Marks.IsDefaultOrEmpty: false } alignment)
        {
            DrawAlignmentFiducials(white, view, alignment.Marks);
        }
    }

    private void DrawAlignmentFiducials(IRenderTexture white, ViewportTransform view, System.Collections.Immutable.ImmutableArray<VisionCanvas.Core.Alignment.AlignedFiducialMark> marks)
    {
        var referenceColor = RenderingConstants.FiducialReferenceColor;
        var detectedColor = RenderingConstants.FiducialDetectedColor;
        var disabledColor = RenderingConstants.FiducialDisabledColor;
        var arm = RenderingConstants.FiducialArmLength;

        for (var i = 0; i < marks.Length; i++)
        {
            var mark = marks[i];
            var refScreen = view.ImageToScreen(mark.Reference);
            _mesh.Line(white, new(refScreen.X - arm, refScreen.Y), new(refScreen.X + arm, refScreen.Y), 1.5, mark.IsEnabled ? referenceColor : disabledColor);
            _mesh.Line(white, new(refScreen.X, refScreen.Y - arm), new(refScreen.X, refScreen.Y + arm), 1.5, mark.IsEnabled ? referenceColor : disabledColor);
            _mesh.Circle(white, refScreen, RenderingConstants.FiducialReferenceRadius, mark.IsEnabled ? referenceColor : disabledColor);

            if (!mark.IsEnabled)
            {
                continue;
            }

            var detScreen = view.ImageToScreen(mark.Detected);
            if ((detScreen - refScreen).Length > RenderingConstants.FiducialMinDisplacementDistance)
            {
                _mesh.Line(white, refScreen, detScreen, 1.8, detectedColor);
            }

            _mesh.Line(white, new(detScreen.X - arm, detScreen.Y - arm), new(detScreen.X + arm, detScreen.Y + arm), 2, detectedColor);
            _mesh.Line(white, new(detScreen.X - arm, detScreen.Y + arm), new(detScreen.X + arm, detScreen.Y - arm), 2, detectedColor);
            _mesh.Circle(white, detScreen, RenderingConstants.FiducialDetectedRadius, detectedColor);
        }
    }

    private void DrawGeometryScreenOutline(IRenderTexture white, RoiGeometry geometry, ViewportTransform view, double thickness, Vector4 color)
    {
        if (geometry.Shape.Kind == RoiShapeKind.Rectangle)
        {
            var corners = geometry.FrameCorners.Select(view, static (v, p) => v.ImageToScreen(p));
            _mesh.Outline(white, corners, thickness, color);
            return;
        }

        var count = geometry.VertexCount;
        Span<PointD> points = count <= 64 ? stackalloc PointD[count] : new PointD[count];
        geometry.CopyOutlineTo(points);
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = view.ImageToScreen(points[i]);
        }

        _mesh.Outline(white, points, thickness, color);
    }

    private void DrawSceneLabelsAndMinimap(RenderScene scene, IRenderTexture white, IReadOnlyDictionary<TileKey, IRenderTexture> tiles,
        Func<string, IRenderTexture> label, Func<string, IRenderTexture?> deferredLabel, IRenderTexture? liveOverview,
        Func<string, GpuTextRegion?>? batchLabel)
    {
        _labels.Draw(_mesh, scene, white, deferredLabel, label);
        if (_labels.HasSummaries)
        {
            var text = label("미세 ROI 요약 · 확대하면 개별 윤곽 표시");
            _mesh.Rectangle(white, new(8, 8, 20 + (text.Width / scene.Dpi), 16 + (text.Height / scene.Dpi)), RenderingConstants.DarkBannerBackground);
            _mesh.Rectangle(text, new(14, 12, 14 + (text.Width / scene.Dpi), 12 + (text.Height / scene.Dpi)), Vector4.One);
        }
        if (!scene.InspectionInLayer && scene.Inspection is { } inspection)
        {
            ResultOverlayPainter.DrawResult(_mesh, scene, inspection, white, label, deferredLabel, batchLabel, _strokeScratch);
        }
        DrawingLayerPainter.Draw(_mesh, scene, white, deferredLabel, label, batchLabel, _strokeScratch);
        IRenderTexture? minimapImage = null;
        if (scene.IsLive)
        {
            minimapImage = liveOverview;
        }
        else if (scene.Image is { } source)
        {
            minimapImage = tiles.GetValueOrDefault(new(source.Levels - 1, 0, 0));
        }
        MinimapPainter.Draw(_mesh, scene, white, minimapImage, label);
    }

    private static bool OpaqueTilesCover(ImageInfo image, IReadOnlyList<TileKey> visible, IReadOnlyDictionary<TileKey, IRenderTexture> tiles)
    {
        foreach (var key in visible)
        {
            if (!tiles.TryGetValue(key, out var texture) || !texture.HasPixels || !texture.IsOpaque)
            {
                return false;
            }
            var factor = 1 << key.Level;
            var width = Math.Min(ImageInfo.TileSize, (int)Math.Ceiling(image.Width / (double)factor) - (key.X * ImageInfo.TileSize));
            var height = Math.Min(ImageInfo.TileSize, (int)Math.Ceiling(image.Height / (double)factor) - (key.Y * ImageInfo.TileSize));
            if (texture.Width < width || texture.Height < height)
            {
                return false;
            }
        }
        return visible.Count > 0;
    }

    internal static void DrawDragFill(SpriteMesh mesh, RenderScene scene, IRenderTexture white)
    {
        if ((scene.Overlay.DragArea ?? scene.Overlay.Marquee) is { } area)
        {
            mesh.Rectangle(white, area, s_accent with
            {
                W = RenderingConstants.DragFillAlpha
            });
        }
    }

    internal static QuadPoints SelectionCorners(BoundsD bounds, ViewportTransform view, RigidTransform2D transform)
        => new(
            view.ImageToScreen(transform.Apply(new(bounds.Left, bounds.Top))),
            view.ImageToScreen(transform.Apply(new(bounds.Right, bounds.Top))),
            view.ImageToScreen(transform.Apply(new(bounds.Right, bounds.Bottom))),
            view.ImageToScreen(transform.Apply(new(bounds.Left, bounds.Bottom))));

    private void Handle(IRenderTexture white, PointD point, double scale, Vector4 color)
    {
        var outer = RenderingConstants.HandleOuterRadius * scale;
        var inner = RenderingConstants.HandleInnerRadius * scale;
        _mesh.Rectangle(white, new(point.X - outer, point.Y - outer, point.X + outer, point.Y + outer), color);
        _mesh.Rectangle(white, new(point.X - inner, point.Y - inner, point.X + inner, point.Y + inner), Vector4.One);
    }

    private void RotationHandle(IRenderTexture white, PointD point, Vector4 color, double scale = HandleGeometry.DefaultScale)
    {
        _mesh.Circle(white, point, RenderingConstants.RotationHandleOuterRadius * scale, color);
        _mesh.Circle(white, point, RenderingConstants.RotationHandleInnerRadius * scale, Vector4.One);
    }
}
