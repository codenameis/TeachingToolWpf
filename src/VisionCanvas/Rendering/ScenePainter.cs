using System.Numerics;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionCanvas.Imaging;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Rendering;

internal sealed class ScenePainter
{
    private readonly SpriteMesh _mesh = new();
    private readonly RoiLabelPainter _labels = new();
    private static readonly Vector4 s_accent = new(.22f, .52f, 1, 1);

    public int RoiBatchBoundary
    {
        get;
        private set;
    }

    public SpriteMesh Build(RenderScene scene, GpuTexture white, IReadOnlyDictionary<TileKey, GpuTexture> tiles, Func<string, GpuTexture> label,
        Func<string, GpuTexture?> deferredLabel, GpuTexture? live, BoundsD liveBounds, GpuFilterPreview filters, GpuTexture? liveOverview,
        Func<string, GpuTextRegion?>? batchLabel = null)
    {
        _mesh.Begin(scene.Width, scene.Height);
        var view = scene.Document.Viewport;
        if (scene.IsLive && live is not null)
        {
            var a = view.ImageToScreen(new(liveBounds.Left, liveBounds.Top));
            var b = view.ImageToScreen(new(liveBounds.Right, liveBounds.Bottom));
            _mesh.Rectangle(live, new(a.X, a.Y, b.X, b.Y), Vector4.One, scene.PointSampling);
        }
        else if (!scene.IsLive && scene.Image is { } image)
        {
            if (tiles.TryGetValue(new(image.Levels - 1, 0, 0), out var overview))
            {
                var topLeft = view.ImageToScreen(new(0, 0));
                var bottomRight = view.ImageToScreen(new(image.Width, image.Height));
                _mesh.Rectangle(overview, new(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y), Vector4.One, scene.PointSampling);
            }
            foreach (var key in TilePlanner.Visible(image, view, scene.Width, scene.Height, scene.Dpi))
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
        foreach (var (region, texture, uv) in filters.Regions)
        {
            var corners = region.Geometry.FrameCorners.Select(view.ImageToScreen).ToArray();
            _mesh.Quad(texture, corners[0], corners[1], corners[2], corners[3], Vector4.One, true, uv);
        }
        RoiBatchBoundary = _mesh.Split();
        if (scene.Overlay.Hover is { } hover && scene.Document.Rois.TryGetValue(hover, out var hovered))
        {
            var style = scene.RoiStyles.For(RoiStyleRoles.Resolve(scene.Document, hover));
            _mesh.Outline(white, (scene.Transform?.Geometry(hovered) ?? hovered.Geometry).Outline.Select(view.ImageToScreen).ToArray(),
                style.StrokeThickness(true), style.SelectedColor);
        }
        if (scene.Document.Selection.Count == 1)
        {
            var roi = scene.Document.Rois[scene.Document.Selection.Single()];
            var color = scene.RoiStyles.For(RoiStyleRoles.Resolve(scene.Document, roi.Id)).SelectedColor;
            var geometry = scene.Transform?.Geometry(roi) ?? roi.Geometry;
            var points = geometry.FrameCorners.Select(view.ImageToScreen).ToArray();
            foreach (var point in HandleGeometry.ResizeHandles(geometry, view))
            {
                Handle(white, point, 6, color);
            }

            var top = (points[0] + points[1]) / 2;
            var rotation = HandleGeometry.RotationHandle(geometry, view);
            _mesh.Line(white, top, rotation, 1, color);
            RotationHandle(white, rotation, color);
        }
        if (scene.Document.Selection.Count > 1)
        {
            var bounds = scene.Document.SelectedSpatial.Bounds;
            var transform = scene.Transform?.Transform ?? default;
            var corners = SelectionCorners(bounds, view, transform);
            _mesh.Outline(white, corners, 1, s_accent);
            var top = (corners[0] + corners[1]) / 2;
            var rotation = top + transform.ApplyVector(new PointD(0, -28));
            _mesh.Line(white, top, rotation, 1, s_accent);
            RotationHandle(white, rotation, s_accent);
        }
        if (scene.Overlay.Draft is { } draft)
        {
            _mesh.Outline(white, draft.Outline.Select(view.ImageToScreen).ToArray(),
            scene.RoiStyles.Default.StrokeThickness(true), scene.RoiStyles.Default.SelectedColor);
        }

        if (!scene.Overlay.PolygonPoints.IsDefaultOrEmpty)
        {
            var points = scene.Overlay.PolygonPoints.Select(view.ImageToScreen).ToArray();
            for (var i = 1; i < points.Length; i++)
            {
                _mesh.Line(white, points[i - 1], points[i], 1.5, s_accent);
            }

            foreach (var point in points)
            {
                Handle(white, point, 5, s_accent);
            }
        }
        if (scene.Overlay.Marquee is { } selection)
        {
            _mesh.Rectangle(white, selection, s_accent with
            {
                W = .08f
            });
            _mesh.Outline(white, [new(selection.Left, selection.Top), new(selection.Right, selection.Top), new(selection.Right, selection.Bottom), new(selection.Left, selection.Bottom)], 1, s_accent);
        }
        _labels.Draw(_mesh, scene, white, deferredLabel, label);
        if (_labels.HasSummaries)
        {
            var text = label("미세 ROI 요약 · 확대하면 개별 윤곽 표시");
            _mesh.Rectangle(white, new(8, 8, 20 + (text.Width / scene.Dpi), 16 + (text.Height / scene.Dpi)), new(.13f, .16f, .22f, .94f));
            _mesh.Rectangle(text, new(14, 12, 14 + (text.Width / scene.Dpi), 12 + (text.Height / scene.Dpi)), Vector4.One);
        }
        ResultOverlayPainter.Draw(_mesh, scene, white, label, deferredLabel, batchLabel);
        var minimapImage = scene.IsLive ? liveOverview : scene.Image is { } source ? tiles.GetValueOrDefault(new(source.Levels - 1, 0, 0)) : null;
        MinimapPainter.Draw(_mesh, scene, white, minimapImage, label);
        return _mesh;
    }

    internal static PointD[] SelectionCorners(BoundsD bounds, ViewportTransform view, RigidTransform2D transform)
        => new[] { new PointD(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom) }
            .Select(point => view.ImageToScreen(transform.Apply(point))).ToArray();

    private void Handle(GpuTexture white, PointD point, double size, Vector4 color)
    {
        _mesh.Rectangle(white, new(point.X - (size / 2) - 1, point.Y - (size / 2) - 1, point.X + (size / 2) + 1, point.Y + (size / 2) + 1), color);
        _mesh.Rectangle(white, new(point.X - (size / 2), point.Y - (size / 2), point.X + (size / 2), point.Y + (size / 2)), Vector4.One);
    }

    private void RotationHandle(GpuTexture white, PointD point, Vector4 color)
    {
        _mesh.Circle(white, point, 5, color);
        _mesh.Circle(white, point, 3.5, Vector4.One);
    }
}
