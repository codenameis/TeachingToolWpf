using System.Numerics;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Rendering;

internal sealed class ScenePainter
{
    private readonly SpriteMesh mesh = new();
    private readonly RoiLabelPainter labels = new();
    private static readonly Vector4 Accent = new(.22f, .52f, 1, 1);
    public int RoiBatchBoundary { get; private set; }
    public SpriteMesh Build(RenderScene scene, GpuTexture white, IReadOnlyDictionary<TileKey, GpuTexture> tiles, Func<string, GpuTexture> label,
        Func<string, GpuTexture?> deferredLabel, GpuTexture? live, BoundsD liveBounds, GpuFilterPreview filters, GpuTexture? liveOverview)
    {
        mesh.Begin(scene.Width, scene.Height);
        var view = scene.Document.Viewport;
        if (scene.IsLive && live is not null)
        {
            var a = view.ImageToScreen(new(liveBounds.Left, liveBounds.Top)); var b = view.ImageToScreen(new(liveBounds.Right, liveBounds.Bottom));
            mesh.Rectangle(live, new(a.X, a.Y, b.X, b.Y), Vector4.One, scene.PointSampling);
        }
        else if (!scene.IsLive && scene.Image is { } image)
        {
            if (tiles.TryGetValue(new(image.Levels - 1, 0, 0), out var overview))
            {
                var topLeft = view.ImageToScreen(new(0, 0)); var bottomRight = view.ImageToScreen(new(image.Width, image.Height));
                mesh.Rectangle(overview, new(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y), Vector4.One, scene.PointSampling);
            }
            foreach (var key in TilePlanner.Visible(image, view, scene.Width, scene.Height, scene.Dpi))
            {
                if (!tiles.TryGetValue(key, out var texture)) continue;
                var factor = 1 << key.Level;
                var a = view.ImageToScreen(new(key.X * 512d * factor, key.Y * 512d * factor));
                var b = view.ImageToScreen(new(Math.Min(image.Width, (key.X * 512d + texture.Width) * factor), Math.Min(image.Height, (key.Y * 512d + texture.Height) * factor)));
                mesh.Rectangle(texture, new(a.X, a.Y, b.X, b.Y), Vector4.One, scene.PointSampling);
            }
        }
        foreach (var (region, texture, uv) in filters.Regions)
        {
            var corners = region.Geometry.Corners.Select(view.ImageToScreen).ToArray();
            mesh.Quad(texture, corners[0], corners[1], corners[2], corners[3], Vector4.One, true, uv);
        }
        RoiBatchBoundary = mesh.Split();
        if (scene.Overlay.Hover is { } hover && scene.Document.Rois.TryGetValue(hover, out var hovered))
            mesh.Outline(white, (scene.Transform?.Geometry(hovered) ?? hovered.Geometry).Corners.Select(view.ImageToScreen).ToArray(), 1, Accent);
        if (scene.Document.Selection.Count == 1)
        {
            var roi = scene.Document.Rois[scene.Document.Selection.Single()];
            var geometry = scene.Transform?.Geometry(roi) ?? roi.Geometry;
            var points = geometry.Corners.Select(view.ImageToScreen).ToArray();
            foreach (var point in HandleGeometry.ResizeHandles(geometry, view)) Handle(white, point, 6, Accent);
            var top = (points[0] + points[1]) / 2;
            var rotation = HandleGeometry.RotationHandle(geometry, view);
            mesh.Line(white, top, rotation, 1, Accent);
            RotationHandle(white, rotation);
        }
        if (scene.Document.Selection.Count > 1)
        {
            var bounds = scene.Document.SelectedSpatial.Bounds;
            var transform = scene.Transform?.Transform ?? default;
            var corners = SelectionCorners(bounds, view, transform);
            mesh.Outline(white, corners, 1, Accent);
            var top = (corners[0] + corners[1]) / 2;
            var rotation = top + transform.ApplyVector(new PointD(0, -28));
            mesh.Line(white, top, rotation, 1, Accent); RotationHandle(white, rotation);
        }
        if (scene.Overlay.Draft is { } draft) mesh.Outline(white, draft.Corners.Select(view.ImageToScreen).ToArray(), 1.5, Accent);
        if (scene.Overlay.Marquee is { } selection)
        {
            mesh.Rectangle(white, selection, Accent with { W = .08f });
            mesh.Outline(white, [new(selection.Left, selection.Top), new(selection.Right, selection.Top), new(selection.Right, selection.Bottom), new(selection.Left, selection.Bottom)], 1, Accent);
        }
        labels.Draw(mesh, scene, white, deferredLabel, label);
        if (labels.HasSummaries)
        {
            var text = label("미세 ROI 요약 · 확대하면 개별 윤곽 표시");
            mesh.Rectangle(white, new(8, 8, 20 + text.Width / scene.Dpi, 16 + text.Height / scene.Dpi), new(.13f, .16f, .22f, .94f));
            mesh.Rectangle(text, new(14, 12, 14 + text.Width / scene.Dpi, 12 + text.Height / scene.Dpi), Vector4.One);
        }
        var minimapImage = scene.IsLive ? liveOverview : scene.Image is { } source ? tiles.GetValueOrDefault(new(source.Levels - 1, 0, 0)) : null;
        MinimapPainter.Draw(mesh, scene, white, minimapImage, label);
        return mesh;
    }
    internal static PointD[] SelectionCorners(BoundsD bounds, ViewportTransform view, RigidTransform2D transform)
        => new[] { new PointD(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top), new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom) }
            .Select(point => view.ImageToScreen(transform.Apply(point))).ToArray();
    private void Handle(GpuTexture white, PointD point, double size, Vector4 color)
    {
        mesh.Rectangle(white, new(point.X - size / 2 - 1, point.Y - size / 2 - 1, point.X + size / 2 + 1, point.Y + size / 2 + 1), color);
        mesh.Rectangle(white, new(point.X - size / 2, point.Y - size / 2, point.X + size / 2, point.Y + size / 2), Vector4.One);
    }
    private void RotationHandle(GpuTexture white, PointD point)
    {
        mesh.Circle(white, point, 5, Accent);
        mesh.Circle(white, point, 3.5, Vector4.One);
    }
}
