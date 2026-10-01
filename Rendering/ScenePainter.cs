using System.Numerics;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using TeachingToolWpf.Core.Imaging;

namespace TeachingToolWpf.Rendering;

internal sealed class ScenePainter
{
    private readonly SpriteMesh mesh = new();
    private static readonly Vector4 Accent = new(.22f, .52f, 1, 1);
    private static readonly Vector4 Outline = new(.66f, .73f, .84f, 1);
    public SpriteMesh Build(RenderScene scene, GpuTexture white, IReadOnlyDictionary<TileKey, GpuTexture> tiles, Func<string, GpuTexture> label, GpuTexture? live, BoundsD liveBounds, GpuFilterPreview filters)
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
        foreach (var (region, texture) in filters.Regions)
        {
            var corners = region.Geometry.Corners.Select(view.ImageToScreen).ToArray();
            mesh.Quad(texture, corners[0], corners[1], corners[2], corners[3], Vector4.One, true);
        }
        var viewport = new BoundsD(-50, -50, scene.Width + 50, scene.Height + 50);
        foreach (var roi in scene.Document.Rois.Values.OrderBy(r => r.Order))
        {
            var points = roi.Geometry.Corners.Select(view.ImageToScreen).ToArray();
            if (!BoundsD.FromPoints(points).Intersects(viewport)) continue;
            var selected = scene.Document.Selection.Contains(roi.Id);
            var hovered = scene.Overlay.Hover == roi.Id;
            var color = selected || hovered ? Accent : Outline;
            mesh.Outline(white, points, selected ? 3 : 2, new(0, 0, 0, .22f));
            mesh.Outline(white, points, selected ? 1.5 : 1, color);
            if (selected && scene.Document.Selection.Count == 1)
            {
                foreach (var point in HandleGeometry.ResizeHandles(roi.Geometry, view)) Handle(white, point, 6, Accent);
                var top = (points[0] + points[1]) / 2;
                var rotation = HandleGeometry.RotationHandle(roi.Geometry, view);
                mesh.Line(white, top, rotation, 1, Accent);
                RotationHandle(white, rotation);
            }
        }
        if (scene.Document.Selection.Count > 1)
        {
            var box = BoundsD.FromPoints(scene.Document.Selection.SelectMany(id => scene.Document.Rois[id].Geometry.Corners).Select(view.ImageToScreen));
            mesh.Outline(white, [new(box.Left, box.Top), new(box.Right, box.Top), new(box.Right, box.Bottom), new(box.Left, box.Bottom)], 1, Accent);
            var top = new PointD(box.Center.X, box.Top); var rotation = top + new PointD(0, -28);
            mesh.Line(white, top, rotation, 1, Accent); RotationHandle(white, rotation);
        }
        if (scene.Overlay.Draft is { } draft) mesh.Outline(white, draft.Corners.Select(view.ImageToScreen).ToArray(), 1.5, Accent);
        if (scene.Overlay.Marquee is { } selection)
        {
            mesh.Rectangle(white, selection, Accent with { W = .08f });
            mesh.Outline(white, [new(selection.Left, selection.Top), new(selection.Right, selection.Top), new(selection.Right, selection.Bottom), new(selection.Left, selection.Bottom)], 1, Accent);
        }
        AddLabels(scene, white, label);
        return mesh;
    }
    private void AddLabels(RenderScene scene, GpuTexture white, Func<string, GpuTexture> rasterize)
    {
        var occupied = new List<BoundsD>();
        var childCounts = scene.Document.Rois.Values.Where(r => r.ParentId is not null).GroupBy(r => r.ParentId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var parents = childCounts.Keys.ToHashSet();
        foreach (var roi in scene.Document.Rois.Values.Where(r => parents.Contains(r.Id) || r.ParentId is null || scene.Document.Selection.Contains(r.Id))
            .OrderByDescending(r => scene.Document.Selection.Contains(r.Id)).ThenBy(r => r.Order))
        {
            var selected = scene.Document.Selection.Contains(roi.Id);
            var bounds = BoundsD.FromPoints(roi.Geometry.Corners.Select(scene.Document.Viewport.ImageToScreen));
            if (!bounds.Intersects(new(0, 0, scene.Width, scene.Height))) continue;
            var name = childCounts.TryGetValue(roi.Id, out var count) ? $"{roi.Name}  [{count}]" : roi.Name;
            var text = selected ? $"{name}  ·  {roi.Geometry.Angle:0.0}°" : name;
            var x = Math.Clamp(bounds.Left, 4, Math.Max(4, scene.Width - 100));
            var anchorY = selected && scene.Document.Selection.Count == 1
                ? Math.Min(bounds.Top, HandleGeometry.RotationHandle(roi.Geometry, scene.Document.Viewport).Y - 8)
                : bounds.Top;
            var y = Math.Clamp(anchorY - 30, 4, Math.Max(4, scene.Height - 30));
            // Suppress overlapping unselected tags before rasterizing them. Selection always wins.
            var estimate = new BoundsD(x, y, Math.Min(scene.Width, x + Math.Min(430, text.Length * 12 + 12)), y + 24);
            if (!(selected && scene.Document.Selection.Count == 1) && occupied.Any(b => b.Intersects(estimate))) continue;
            if (occupied.Count >= 256) continue;
            var texture = rasterize(text);
            var box = new BoundsD(x, y, Math.Min(scene.Width, x + texture.Width / scene.Dpi + 12), y + texture.Height / scene.Dpi + 8);
            occupied.Add(box);
            mesh.Rectangle(white, box, selected ? Accent : new(.13f, .16f, .22f, .90f));
            mesh.Rectangle(texture, new(x + 6, y + 4, x + 6 + texture.Width / scene.Dpi, y + 4 + texture.Height / scene.Dpi), Vector4.One);
        }
    }
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
