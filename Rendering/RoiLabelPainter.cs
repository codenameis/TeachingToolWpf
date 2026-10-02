using System.Numerics;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Rendering;

/// <summary>Retains label placement during nearby pans. Candidate geometry and label rasterization have separate lifetimes.</summary>
internal sealed class RoiLabelPainter
{
    private readonly record struct Label(Guid Id, string Text, PointD Anchor, BoundsD ImageBounds, bool Selected);
    private readonly RoiVisibility visible = new(), selectedVisible = new(), movingVisible = new();
    private readonly List<Label> labels = [];
    private readonly Dictionary<(int X, int Y), List<BoundsD>> occupied = [];
    private RenderScene? previous;
    public bool HasSummaries { get; private set; }
    public void Draw(SpriteMesh mesh, RenderScene scene, GpuTexture white, Func<string, GpuTexture?> rasterize, Func<string, GpuTexture> immediate)
    {
        var offset = previous is null ? default : scene.Document.Viewport.Offset - previous.Document.Viewport.Offset;
        var moved = (scene.Translation?.Delta ?? default) - (previous?.Translation?.Delta ?? default);
        if (previous is null || !ReferenceEquals(previous.Document.Rois, scene.Document.Rois) ||
            !ReferenceEquals(previous.Document.Selection, scene.Document.Selection) || previous.Document.Viewport.Scale != scene.Document.Viewport.Scale ||
            !ReferenceEquals(previous.Translation?.Selection, scene.Translation?.Selection) ||
            previous.Width != scene.Width || previous.Height != scene.Height || previous.Dpi != scene.Dpi ||
            Math.Abs(offset.X) > 64 || Math.Abs(offset.Y) > 64 || moved.Length * scene.Document.Viewport.Scale > 64)
        { Rebuild(scene); previous = scene; offset = default; moved = default; }
        occupied.Clear();
        foreach (var label in labels)
        {
            var moving = scene.Translation is { } translation && translation.Selection.Affected.Contains(label.Id);
            var movement = moving ? moved * scene.Document.Viewport.Scale : default;
            var displayView = moving ? scene.Document.Viewport.Pan(scene.Translation!.Delta * scene.Document.Viewport.Scale) : scene.Document.Viewport;
            if (!IsVisible(label.ImageBounds, displayView, scene.Width, scene.Height)) continue;
            var anchor = ClampAnchor(label.Anchor + offset + movement, scene.Width, scene.Height);
            var texture = label.Selected && scene.Document.Selection.Count == 1 ? immediate(label.Text) : rasterize(label.Text);
            if (texture is null) continue;
            var box = new BoundsD(anchor.X, anchor.Y, Math.Min(scene.Width, anchor.X + texture.Width / scene.Dpi + 12), anchor.Y + texture.Height / scene.Dpi + 8);
            if (Intersects(box)) continue;
            Occupy(box);
            mesh.Rectangle(white, box, label.Selected ? new(.22f, .52f, 1, 1) : new(.13f, .16f, .22f, .90f));
            mesh.Rectangle(texture, new(box.Left + 6, box.Top + 4, box.Left + 6 + texture.Width / scene.Dpi, box.Top + 4 + texture.Height / scene.Dpi), Vector4.One);
        }
    }
    private void Rebuild(RenderScene scene)
    {
        labels.Clear(); occupied.Clear();
        var state = scene.Document; var view = state.Viewport;
        var translation = scene.Translation;
        var movingView = view.Pan((translation?.Delta ?? default) * view.Scale);
        visible.Update(translation?.Selection.Stationary ?? state.Snapshot.Spatial, view, scene.Width, scene.Height);
        HasSummaries = visible.Summaries.Count > 0;
        if (translation is null && state.Selection.Count == state.Rois.Count) Add(visible.Rois, true, view);
        else
        {
            if (state.Selection.Count > 0)
            {
                selectedVisible.Update(state.SelectedSpatial, movingView, scene.Width, scene.Height);
                HasSummaries |= selectedVisible.Summaries.Count > 0;
                Add(selectedVisible.Rois, true, movingView);
            }
            if (translation is not null && translation.Selection.Affected.Count > state.Selection.Count)
            {
                movingVisible.Update(translation.Selection.Moving, movingView, scene.Width, scene.Height);
                HasSummaries |= movingVisible.Summaries.Count > 0;
                Add(movingVisible.Rois, false, movingView);
            }
            Add(visible.Rois, false, view);
        }
        void Add(IEnumerable<Roi> candidates, bool selected, ViewportTransform displayView)
        {
            var children = state.Snapshot.Children;
            foreach (var roi in candidates)
            {
                if (labels.Count >= 256) break;
                if (!selected && (state.Selection.Contains(roi.Id) || roi.ParentId is not null && !children.ContainsKey(roi.Id))) continue;
                var image = roi.Geometry.Bounds;
                var a = displayView.ImageToScreen(new(image.Left, image.Top)); var b = displayView.ImageToScreen(new(image.Right, image.Bottom));
                if (b.X < -64 || a.X > scene.Width + 64 || b.Y < -64 || a.Y > scene.Height + 64) continue;
                var x = Math.Clamp(a.X, 4, Math.Max(4, scene.Width - 100));
                var top = selected && state.Selection.Count == 1 ? Math.Min(a.Y, HandleGeometry.RotationHandle(roi.Geometry, displayView).Y - 8) : a.Y;
                var y = Math.Clamp(top - 30, 4, Math.Max(4, scene.Height - 30));
                var estimatedLength = roi.Name.Length + (children.ContainsKey(roi.Id) ? 12 : 0) + (selected ? 14 : 0);
                var estimate = new BoundsD(x, y, Math.Min(scene.Width, x + Math.Min(430, estimatedLength * 12 + 12)), y + 24);
                if (Intersects(estimate)) continue;
                var name = children.TryGetValue(roi.Id, out var descendants) ? $"{roi.Name}  [{descendants.Length}]" : roi.Name;
                var text = selected ? $"{name}  ·  {roi.Geometry.Angle:0.0}°" : name;
                labels.Add(new(roi.Id, text, new(a.X, top - 30), image, selected)); Occupy(estimate);
            }
        }
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
