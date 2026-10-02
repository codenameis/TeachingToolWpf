using System.Numerics;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Rendering;

/// <summary>Retains label placement during nearby pans. Candidate geometry and label rasterization have separate lifetimes.</summary>
internal sealed class RoiLabelPainter
{
    private readonly record struct Label(Roi Roi, string Text, bool Selected);
    private readonly RoiVisibility visible = new(), selectedVisible = new(), movingVisible = new();
    private readonly List<Label> labels = [];
    private readonly Dictionary<(int X, int Y), List<BoundsD>> occupied = [];
    private RenderScene? previous;
    public bool HasSummaries { get; private set; }
    public void Draw(SpriteMesh mesh, RenderScene scene, GpuTexture white, Func<string, GpuTexture?> rasterize, Func<string, GpuTexture> immediate)
    {
        var offset = previous is null ? default : scene.Document.Viewport.Offset - previous.Document.Viewport.Offset;
        var transform = scene.Transform?.Transform ?? default;
        var priorTransform = previous?.Transform?.Transform ?? default;
        var moved = transform.Translation - priorTransform.Translation;
        if (previous is null || !ReferenceEquals(previous.Document.Rois, scene.Document.Rois) ||
            !ReferenceEquals(previous.Document.Selection, scene.Document.Selection) || previous.Document.Viewport.Scale != scene.Document.Viewport.Scale ||
            !ReferenceEquals(previous.Transform?.Selection, scene.Transform?.Selection) ||
            transform.AngleDegrees != priorTransform.AngleDegrees || transform.Pivot != priorTransform.Pivot ||
            previous.Width != scene.Width || previous.Height != scene.Height || previous.Dpi != scene.Dpi ||
            Math.Abs(offset.X) > 64 || Math.Abs(offset.Y) > 64 || moved.Length * scene.Document.Viewport.Scale > 64)
        { Rebuild(scene); previous = scene; }
        occupied.Clear();
        foreach (var label in labels)
        {
            var geometry = scene.Transform?.Geometry(label.Roi) ?? label.Roi.Geometry;
            if (!IsVisible(geometry.Bounds, scene.Document.Viewport, scene.Width, scene.Height)) continue;
            var anchor = ClampAnchor(Anchor(geometry, scene.Document.Viewport, label.Selected && scene.Document.Selection.Count == 1), scene.Width, scene.Height);
            var texture = label.Selected && scene.Document.Selection.Count == 1 ? immediate(label.Text) : rasterize(label.Text);
            if (texture is null) continue;
            var box = new BoundsD(anchor.X, anchor.Y, Math.Min(scene.Width, anchor.X + texture.Width / scene.Dpi + 12), anchor.Y + texture.Height / scene.Dpi + 8);
            if (Intersects(box)) continue;
            Occupy(box);
            var background = label.Selected ? scene.RoiStyles.For(RoiStyleRoles.Resolve(scene.Document, label.Roi.Id)).SelectedColor : new Vector4(.13f, .16f, .22f, .90f);
            mesh.Rectangle(white, box, background);
            mesh.Rectangle(texture, new(box.Left + 6, box.Top + 4, box.Left + 6 + texture.Width / scene.Dpi, box.Top + 4 + texture.Height / scene.Dpi), Vector4.One);
        }
    }
    private void Rebuild(RenderScene scene)
    {
        labels.Clear(); occupied.Clear();
        var state = scene.Document; var view = state.Viewport;
        var preview = scene.Transform;
        var transform = preview?.Transform ?? default;
        visible.Update(preview?.Selection.Stationary ?? state.Snapshot.Spatial, view, scene.Width, scene.Height);
        HasSummaries = visible.Summaries.Count > 0;
        if (preview is null && state.Selection.Count == state.Rois.Count) Add(visible.Rois, true);
        else
        {
            if (state.Selection.Count > 0)
            {
                selectedVisible.Update(state.SelectedSpatial, view, scene.Width, scene.Height, transform);
                HasSummaries |= selectedVisible.Summaries.Count > 0;
                Add(selectedVisible.Rois, true);
            }
            if (preview is not null && preview.Selection.Affected.Count > state.Selection.Count)
            {
                movingVisible.Update(preview.Selection.Moving, view, scene.Width, scene.Height, transform);
                HasSummaries |= movingVisible.Summaries.Count > 0;
                Add(movingVisible.Rois, false);
            }
            Add(visible.Rois, false);
        }
        void Add(IEnumerable<Roi> candidates, bool selected)
        {
            var children = state.Snapshot.Children;
            foreach (var roi in candidates)
            {
                if (labels.Count >= 256) break;
                if (!selected && (state.Selection.Contains(roi.Id) || roi.ParentId is not null && !children.ContainsKey(roi.Id))) continue;
                var geometry = preview?.Geometry(roi) ?? roi.Geometry;
                if (!IsVisible(geometry.Bounds, view, scene.Width, scene.Height)) continue;
                var anchor = ClampAnchor(Anchor(geometry, view, selected && state.Selection.Count == 1), scene.Width, scene.Height);
                var estimatedLength = roi.Name.Length + (children.ContainsKey(roi.Id) ? 12 : 0) + (selected ? 14 : 0);
                var estimate = new BoundsD(anchor.X, anchor.Y, Math.Min(scene.Width, anchor.X + Math.Min(430, estimatedLength * 12 + 12)), anchor.Y + 24);
                if (Intersects(estimate)) continue;
                var name = children.TryGetValue(roi.Id, out var descendants) ? $"{roi.Name}  [{descendants.Length}]" : roi.Name;
                var text = selected ? $"{name}  ·  {geometry.Angle:0.0}°" : name;
                labels.Add(new(roi, text, selected)); Occupy(estimate);
            }
        }
    }
    private static PointD Anchor(RotatedRect geometry, ViewportTransform view, bool singleSelection)
    {
        var bounds = geometry.Bounds;
        var corner = view.ImageToScreen(new(bounds.Left, bounds.Top));
        var top = singleSelection ? Math.Min(corner.Y, HandleGeometry.RotationHandle(geometry, view).Y - 8) : corner.Y;
        return new(corner.X, top - 30);
    }
    private bool Intersects(BoundsD bounds)
    {
        foreach (var cell in Cells(bounds))
            if (occupied.TryGetValue(cell, out var list)) foreach (var other in list) if (other.Intersects(bounds)) return true;
        return false;
    }
    internal static PointD ClampAnchor(PointD anchor, double width, double height)
        => new(Math.Clamp(anchor.X, 4, Math.Max(4, width - 100)), Math.Clamp(anchor.Y, 4, Math.Max(4, height - 30)));
    internal static bool IsVisible(BoundsD bounds, ViewportTransform view, double width, double height)
    {
        var a = view.ImageToScreen(new(bounds.Left, bounds.Top)); var b = view.ImageToScreen(new(bounds.Right, bounds.Bottom));
        return b.X >= -64 && a.X <= width + 64 && b.Y >= -64 && a.Y <= height + 64;
    }
    private void Occupy(BoundsD box)
    {
        foreach (var cell in Cells(box))
        { if (!occupied.TryGetValue(cell, out var list)) occupied[cell] = list = []; list.Add(box); }
    }
    private static IEnumerable<(int X, int Y)> Cells(BoundsD bounds)
    {
        for (var y = (int)Math.Floor(bounds.Top / 32); y <= (int)Math.Floor(bounds.Bottom / 32); y++)
        for (var x = (int)Math.Floor(bounds.Left / 128); x <= (int)Math.Floor(bounds.Right / 128); x++) yield return (x, y);
    }
}
