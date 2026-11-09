using System.Numerics;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace VisionCanvas.Rendering;

/// <summary>Retains label placement during nearby pans. Candidate geometry and label rasterization have separate lifetimes.</summary>
internal sealed class RoiLabelPainter
{
    private readonly record struct Label(Roi Roi, string Text, bool Selected);
    private readonly RoiVisibility _visible = new();
    private readonly RoiVisibility _selectedVisible = new();
    private readonly RoiVisibility _movingVisible = new();
    private readonly List<Label> _labels = [];
    private readonly Dictionary<(int X, int Y), List<BoundsD>> _occupied = [];
    private readonly Stack<List<BoundsD>> _listPool = [];
    private RenderScene? _previous;

    public bool HasSummaries
    {
        get;
        private set;
    }

    public void Draw(SpriteMesh mesh, RenderScene scene, IRenderTexture white, Func<string, IRenderTexture?> rasterize, Func<string, IRenderTexture> immediate)
    {
        var offset = _previous is null ? default : scene.Document.Viewport.Offset - _previous.Document.Viewport.Offset;
        var transform = scene.Transform?.Transform ?? default;
        var priorTransform = _previous?.Transform?.Transform ?? default;
        var moved = transform.Translation - priorTransform.Translation;
        if (_previous is null || !ReferenceEquals(_previous.Document.Rois, scene.Document.Rois) ||
            !ReferenceEquals(_previous.Document.Selection, scene.Document.Selection) || _previous.Document.Viewport.Scale != scene.Document.Viewport.Scale ||
            !ReferenceEquals(_previous.Transform?.Selection, scene.Transform?.Selection) ||
            !ReferenceEquals(_previous.Alignment, scene.Alignment) ||
            transform.AngleDegrees != priorTransform.AngleDegrees || transform.Pivot != priorTransform.Pivot ||
            _previous.Width != scene.Width || _previous.Height != scene.Height || _previous.Dpi != scene.Dpi ||
            _previous.Overlay.HandleScale != scene.Overlay.HandleScale ||
            Math.Abs(offset.X) > RenderingConstants.LabelPanInvalidationThresholdPixels ||
            Math.Abs(offset.Y) > RenderingConstants.LabelPanInvalidationThresholdPixels ||
            moved.Length * scene.Document.Viewport.Scale > RenderingConstants.LabelPanInvalidationThresholdPixels)
        {
            Rebuild(scene);
            _previous = scene;
        }

        ClearOccupied();
        foreach (var label in _labels)
        {
            var geometry = scene.EffectiveGeometry(label.Roi);
            if (!IsVisible(geometry.Bounds, scene.Document.Viewport, scene.Width, scene.Height))
            {
                continue;
            }

            var anchor = ClampAnchor(Anchor(geometry, scene.Document.Viewport, label.Selected && scene.Document.SelectionCount == 1, scene.Overlay.HandleScale), scene.Width, scene.Height);
            var texture = label.Selected && scene.Document.SelectionCount == 1 ? immediate(label.Text) : rasterize(label.Text);
            if (texture is null)
            {
                continue;
            }

            var box = new BoundsD(anchor.X, anchor.Y, Math.Min(scene.Width, anchor.X + (texture.Width / scene.Dpi) + 12), anchor.Y + (texture.Height / scene.Dpi) + 8);
            if (Intersects(box))
            {
                continue;
            }

            Occupy(box);
            var background = label.Selected ? scene.RoiStyles.For(RoiStyleRoles.Resolve(scene.Document, label.Roi.Id)).StrokeColor(true, scene.Document.SelectionCount > 1) : RenderingConstants.DarkPanelBackground;
            var luminance = (background.X * RenderingConstants.Bt601LuminanceRed) +
                            (background.Y * RenderingConstants.Bt601LuminanceGreen) +
                            (background.Z * RenderingConstants.Bt601LuminanceBlue);
            var foreground = luminance > RenderingConstants.LightBackgroundThreshold
                ? RenderingConstants.DarkHighContrastForeground : Vector4.One;
            mesh.Rectangle(white, box, background);
            mesh.Rectangle(texture, new(box.Left + 6, box.Top + 4, box.Left + 6 + (texture.Width / scene.Dpi), box.Top + 4 + (texture.Height / scene.Dpi)), foreground);
        }
    }

    private void Rebuild(RenderScene scene)
    {
        _labels.Clear();
        ClearOccupied();
        var state = scene.Document;
        var view = state.Viewport;
        var preview = scene.Transform;
        var alignment = scene.Alignment;
        var transform = preview?.Transform ?? (alignment is { HasGroupAlignments: false } ? alignment.OverallTransform : default);

        if (preview is null && alignment is { HasGroupAlignments: true })
        {
            HasSummaries = false;
            foreach (var partition in state.Snapshot.AlignmentPartitions)
            {
                var scopeTransform = alignment.GetScopeTransform(partition.ScopeId);
                _visible.Update(partition.Spatial, view, scene.Width, scene.Height, scopeTransform);
                HasSummaries |= _visible.Summaries.Count > 0;
                Add(_visible.Rois, false);
            }

            return;
        }

        _visible.Update(preview?.Selection.Stationary ?? state.Snapshot.Spatial, view, scene.Width, scene.Height, preview is null ? transform : default);
        HasSummaries = _visible.Summaries.Count > 0;
        if (preview is null && state.Selection.Count == state.Rois.Count)
        {
            Add(_visible.Rois, true);
        }
        else
        {
            if (state.Selection.Count > 0)
            {
                _selectedVisible.Update(state.SelectedSpatial, view, scene.Width, scene.Height, transform);
                HasSummaries |= _selectedVisible.Summaries.Count > 0;
                Add(_selectedVisible.Rois, true);
            }
            if (preview is not null && preview.Selection.Affected.Count > state.Selection.Count)
            {
                _movingVisible.Update(preview.Selection.Moving, view, scene.Width, scene.Height, transform);
                HasSummaries |= _movingVisible.Summaries.Count > 0;
                Add(_movingVisible.Rois, false);
            }
            Add(_visible.Rois, false);
        }

        void Add(IEnumerable<Roi> candidates, bool selected)
        {
            var children = state.Snapshot.Children;
            foreach (var roi in candidates)
            {
                if (_labels.Count >= RenderingConstants.MaxScreenLabelCount)
                {
                    break;
                }

                if (!roi.IsNameVisible || (!selected && (state.Selection.Contains(roi.Id) || (roi.ParentId is not null && !roi.IsGroup && !children.ContainsKey(roi.Id)))))
                {
                    continue;
                }

                var geometry = scene.EffectiveGeometry(roi);
                if (!IsVisible(geometry.Bounds, view, scene.Width, scene.Height))
                {
                    continue;
                }

                var anchor = ClampAnchor(Anchor(geometry, view, selected && state.SelectionCount == 1, scene.Overlay.HandleScale), scene.Width, scene.Height);
                var estimatedLength = roi.Name.Length + (children.ContainsKey(roi.Id) ? 12 : 0) + (selected ? 14 : 0);
                var estimate = new BoundsD(anchor.X, anchor.Y, Math.Min(scene.Width, anchor.X + Math.Min(RenderingConstants.MaxLabelWidth, (estimatedLength * 12) + 12)), anchor.Y + 24);
                if (Intersects(estimate))
                {
                    continue;
                }

                var name = children.TryGetValue(roi.Id, out var descendants) ? $"{roi.Name}  [{descendants.Length}]" : roi.Name;
                var text = selected ? $"{name}  ·  {geometry.Angle:0.0}°" : name;
                _labels.Add(new(roi, text, selected));
                Occupy(estimate);
            }
        }
    }

    private static PointD Anchor(RoiGeometry geometry, ViewportTransform view, bool singleSelection, double handleScale)
    {
        var bounds = geometry.Bounds;
        var corner = view.ImageToScreen(new(bounds.Left, bounds.Top));
        var top = singleSelection ? Math.Min(corner.Y, HandleGeometry.RotationHandle(geometry, view, handleScale).Y - (8 * handleScale)) : corner.Y;
        return new(corner.X, top - RenderingConstants.LabelAnchorTopOffset);
    }

    private bool Intersects(BoundsD bounds)
    {
        GetCellRange(bounds, out var minX, out var maxX, out var minY, out var maxY);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                if (_occupied.TryGetValue((x, y), out var list))
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (list[i].Intersects(bounds))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    internal static PointD ClampAnchor(PointD anchor, double width, double height)
        => new(
            Math.Clamp(anchor.X, RenderingConstants.LabelScreenMarginLeft, Math.Max(RenderingConstants.LabelScreenMarginLeft, width - RenderingConstants.LabelScreenSafetyMarginRight)),
            Math.Clamp(anchor.Y, RenderingConstants.LabelScreenMarginLeft, Math.Max(RenderingConstants.LabelScreenMarginLeft, height - RenderingConstants.LabelAnchorTopOffset)));

    internal static bool IsVisible(BoundsD bounds, ViewportTransform view, double width, double height)
    {
        var margin = RenderingConstants.ViewportCullingMarginPixels;
        var a = view.ImageToScreen(new(bounds.Left, bounds.Top));
        var b = view.ImageToScreen(new(bounds.Right, bounds.Bottom));
        return b.X >= -margin && a.X <= width + margin && b.Y >= -margin && a.Y <= height + margin;
    }

    private void Occupy(BoundsD box)
    {
        GetCellRange(box, out var minX, out var maxX, out var minY, out var maxY);
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var cell = (x, y);
                if (!_occupied.TryGetValue(cell, out var list))
                {
                    list = _listPool.Count > 0 ? _listPool.Pop() : [];
                    _occupied[cell] = list;
                }

                list.Add(box);
            }
        }
    }

    private void ClearOccupied()
    {
        foreach (var list in _occupied.Values)
        {
            list.Clear();
            _listPool.Push(list);
        }

        _occupied.Clear();
    }

    private static void GetCellRange(BoundsD bounds, out int minX, out int maxX, out int minY, out int maxY)
    {
        // [Domain Why]: 공간 상 점유 격자를 셀 단위로 나누어 라벨 간 겹침(Collision) 여부를 O(1) 수준으로 검사한다.
        minX = (int)Math.Floor(bounds.Left / RenderingConstants.LabelOccupancyCellWidth);
        maxX = (int)Math.Floor(bounds.Right / RenderingConstants.LabelOccupancyCellWidth);
        minY = (int)Math.Floor(bounds.Top / RenderingConstants.LabelOccupancyCellHeight);
        maxY = (int)Math.Floor(bounds.Bottom / RenderingConstants.LabelOccupancyCellHeight);
    }
}
