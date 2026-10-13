using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Interaction;

public enum EditorTool
{
    Select, Pan, Draw, Mask
}
[Flags]
public enum InputModifiers
{
    None = 0, Control = 1, Shift = 2, Alt = 4
}
public sealed record InteractionOverlay(Guid? Hover = null, RoiGeometry? Draft = null, BoundsD? Marquee = null, ImmutableArray<PointD> PolygonPoints = default)
{
    /// <summary>ROI 생성 중 마우스로 지정하는 임시 사각 영역(DIP). 문서 형상은 아니다.</summary>
    public BoundsD? DragArea
    {
        get; init;
    }

    public MaskRegion? MaskDraft
    {
        get; init;
    }
}

/// <summary>Chooses an input state; each gesture owns its preview and commit/cancel behavior.</summary>
public sealed class InteractionController(EditorSession editor, Action<string>? commitPreview = null, Action<IEnumerable<Guid>>? selectRois = null,
    Action<PointD>? previewTranslation = null, Action<PointD, double>? previewRotation = null, Action<BoundsD, bool>? selectRegion = null) : IViewportInput
{
    private IGesture? _gesture;
    private EditorTool _tool;
    private RoiShapeKind _drawShape;
    private readonly List<PointD> _polygonPoints = [];
    private ImageReference? _polygonImage;
    private Guid? _drawParentId;

    public Guid? MaskTargetId
    {
        get; private set;
    }
    public MaskShape MaskShape
    {
        get; private set;
    }

    public void StartMask(MaskShape shape)
    {
        if (NavigationOnly || editor.IsPreviewing || editor.State.SelectionCount != 1 || editor.State.Selection.Count != 1)
        {
            return;
        }
        var id = editor.State.Selection.Single();
        if (editor.State.IsGroupingRoi(id) || editor.State.Rois[id].Masks.Length >= EditorSession.MaximumMasksPerRoi)
        {
            return;
        }
        MaskTargetId = id;
        MaskShape = shape;
        Tool = EditorTool.Mask;
    }

    public Guid? DrawParentId
    {
        get => _drawParentId;
        set
        {
            Cancel();
            _drawParentId = value;
            Changed?.Invoke();
        }
    }

    internal void AddDrawnRoi(RoiGeometry geometry)
    {
        if (DrawParentId is { } parentId)
        {
            editor.AddChild(parentId, geometry);
        }
        else
        {
            editor.Add(geometry);
        }
    }

    public RoiShapeKind DrawShape
    {
        get => _drawShape;
        set
        {
            Cancel();
            _drawShape = value;
            Changed?.Invoke();
        }
    }

    public int PolygonPointCount => _polygonPoints.Count;

    public bool CanCompletePolygon => _polygonPoints.Count >= 3;

    private double _viewWidth;

    private double _viewHeight;

    public MinimapLayout? Minimap => editor.State.Image is { } image ? MinimapLayout.Create(image.Width, image.Height, _viewWidth, _viewHeight) : null;

    public void SetViewportSize(double width, double height)
    {
        if (width == _viewWidth && height == _viewHeight)
        {
            return;
        }

        _viewWidth = width;
        _viewHeight = height;
        if (_gesture is MinimapGesture || _polygonPoints.Count > 0)
        {
            Cancel();
        }
    }

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            Cancel();
            _tool = value;
            Changed?.Invoke();
        }
    }

    public InteractionOverlay Overlay
    {
        get;
        private set;
    } = new();

    public bool IsDragging => _gesture is not null;

    public bool NavigationOnly
    {
        get;
        set;
    }

    public event Action? Changed;

    internal EditorSession Editor => editor;

    internal void PreviewTranslation(PointD delta)
    {
        if (previewTranslation is null)
        {
            editor.PreviewTranslation(delta);
        }
        else
        {
            previewTranslation(delta);
        }
    }

    internal void PreviewRotation(PointD pivot, double degrees)
    {
        if (previewRotation is null)
        {
            editor.PreviewRotation(pivot, degrees);
        }
        else
        {
            previewRotation(pivot, degrees);
        }
    }

    internal void CommitPreview(string name)
    {
        if (commitPreview is null)
        {
            editor.CommitPreview(name);
        }
        else
        {
            commitPreview(name);
        }
    }

    internal void Select(IEnumerable<Guid> ids)
    {
        if (selectRois is null)
        {
            editor.Select(ids);
        }
        else
        {
            selectRois(ids);
        }
    }

    internal void Show(InteractionOverlay overlay)
    {
        Overlay = overlay;
        Changed?.Invoke();
    }

    internal void SelectRegion(DocumentState baseline, BoundsD bounds, bool additive)
    {
        if (selectRegion is not null)
        {
            selectRegion(bounds, additive);
        }
        else if (!baseline.RepeatedRois.IsEmpty)
        {
            editor.SelectRegion(bounds, additive);
        }
        else
        {
            var ids = baseline.Snapshot.Spatial.Intersecting(bounds).Where(roi => roi.Geometry.Bounds is var box &&
                bounds.Contains(new(box.Left, box.Top)) && bounds.Contains(new(box.Right, box.Bottom))).Select(roi => roi.Id);
            Select(additive ? baseline.Selection.Union(ids) : ids);
        }
    }

    public void Down(PointD screen, InputModifiers modifiers = InputModifiers.None, bool middle = false)
    {
        ValidateDraftImage();
        if (!middle && !NavigationOnly && Tool == EditorTool.Draw && DrawShape == RoiShapeKind.Polygon &&
            !(Minimap is { } panel && panel.Panel.Contains(screen)))
        {
            _gesture?.Cancel();
            _gesture = new NoGesture();
            var point = RoiImageBounds.Clamp(editor.State.Viewport.ScreenToImage(screen), editor.State.Image);
            if (PolygonClosure.Find(_polygonPoints, point) is { } closed)
            {
                CompletePolygon(closed);
                return;
            }

            if (CanCompletePolygon && (editor.State.Viewport.ImageToScreen(_polygonPoints[0]) - screen).Length < 8)
            {
                CompletePolygon();
                return;
            }

            if (_polygonPoints.Count == 0)
            {
                _polygonImage = editor.State.Image;
            }

            if (_polygonPoints.Count == 0 || (_polygonPoints[^1] - point).Length > .000001)
            {
                _polygonPoints.Add(point);
            }

            ShowPolygon();
            return;
        }
        Cancel();
        if (!middle && Minimap is { } map && map.Panel.Contains(screen))
        {
            _gesture = map.ImageBounds.Contains(screen) ? new MinimapGesture(editor, map, screen) : new NoGesture();
        }
        else if (middle || Tool == EditorTool.Pan)
        {
            _gesture = new PanGesture(editor, screen);
        }
        else if (NavigationOnly)
        {
            _gesture = new NoGesture();
        }
        else if (Tool == EditorTool.Draw)
        {
            _gesture = new DrawGesture(this, screen);
        }
        else if (Tool == EditorTool.Mask && MaskTargetId is { } maskTarget && editor.State.Rois.TryGetValue(maskTarget, out var owner))
        {
            _gesture = new MaskDrawGesture(this, owner, screen);
        }
        else
        {
            _gesture = StartSelection(screen, modifiers);
        }

        Changed?.Invoke();
    }

    public void DoubleClick(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        if (Tool != EditorTool.Select || NavigationOnly ||
            (Minimap is { } map && map.Panel.Contains(screen)))
        {
            Down(screen, modifiers);
            return;
        }

        Cancel();
        // 더블클릭은 기존 선택 핸들보다 실제 ROI hit를 우선하고 선택만 교체한다.
        var hit = SelectionHit(screen, modifiers.HasFlag(InputModifiers.Alt));
        if (hit.Repeated is { } repeated)
        {
            editor.SelectRepeated(repeated);
        }
        else if (hit.Ordinary is { } id)
        {
            Select([id]);
        }
        _gesture = new NoGesture();
        Changed?.Invoke();
    }

    private IGesture StartSelection(PointD screen, InputModifiers modifiers)
    {
        var state = editor.State;
        var cycle = modifiers.HasFlag(InputModifiers.Alt);
        if (!cycle && state.SingleRepeatedCell is { } cell)
        {
            var geometry = state.RepeatedRois[cell.BatchId].Geometry(cell.Index);
            if ((HandleGeometry.RotationHandle(geometry, state.Viewport) - screen).Length < 10)
            {
                return new RotateGesture(this, screen, geometry.Center);
            }

            var handles = HandleGeometry.ResizeHandles(geometry, state.Viewport);
            for (var i = 0; i < handles.Count; i++)
            {
                if ((handles[i] - screen).Length < 9)
                {
                    return new RepeatedResizeGesture(this, cell, geometry, i, screen);
                }
            }
        }
        else if (!cycle && state.SelectionCount == 1)
        {
            var selected = state.Rois[state.Selection.Single()];
            if ((HandleGeometry.RotationHandle(selected.Geometry, state.Viewport) - screen).Length < 10)
            {
                return new RotateGesture(this, screen, selected.Geometry.Center);
            }

            var handles = HandleGeometry.ResizeHandles(selected.Geometry, state.Viewport);
            for (var i = 0; i < handles.Count; i++)
            {
                if ((handles[i] - screen).Length < 9)
                {
                    return new ResizeGesture(this, selected, i, screen);
                }
            }
        }
        else if (!cycle && state.SelectionCount > 1)
        {
            var bounds = SelectionBounds(state);
            if ((state.Viewport.ImageToScreen(new(bounds.Center.X, bounds.Top)) + new PointD(0, -28) - screen).Length < 10)
            {
                return new RotateGesture(this, screen, bounds.Center);
            }
        }
        var hit = SelectionHit(screen, cycle);
        if (hit.Repeated is { } repeatedHit)
        {
            if (modifiers.HasFlag(InputModifiers.Control))
            {
                editor.SelectRepeated(repeatedHit, additive: true, toggle: true);
                return new NoGesture();
            }
            if (!state.RepeatedSelection.TryGetValue(repeatedHit.BatchId, out var cells) || !cells.Contains(repeatedHit.Index))
            {
                editor.SelectRepeated(repeatedHit, additive: modifiers.HasFlag(InputModifiers.Shift));
            }

            return new MoveGesture(this, screen);
        }
        if (hit.Ordinary is { } id)
        {
            if (modifiers.HasFlag(InputModifiers.Control))
            {
                editor.Select(state.Selection.Contains(id) ? state.Selection.Remove(id) : state.Selection.Add(id), preserveRepeated: true);
                return new NoGesture();
            }
            if (!state.Selection.Contains(id))
            {
                editor.Select(modifiers.HasFlag(InputModifiers.Shift) ? state.Selection.Add(id) : [id], preserveRepeated: modifiers.HasFlag(InputModifiers.Shift));
            }

            return new MoveGesture(this, screen);
        }
        return new MarqueeGesture(this, screen, modifiers != InputModifiers.None);
    }

    public void Move(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        ValidateDraftImage();
        if (_gesture is { } active)
        {
            active.Move(screen, modifiers);
        }
        else if (_polygonPoints.Count > 0)
        {
            ShowPolygon(RoiImageBounds.Clamp(editor.State.Viewport.ScreenToImage(screen), editor.State.Image));
        }
        else if (!NavigationOnly && Tool != EditorTool.Mask)
        {
            var hit = Hit(screen);
            if (Overlay.Hover != hit)
            {
                Show(Overlay with
                {
                    Hover = hit
                });
            }
        }
    }

    public void Up(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        var active = _gesture;
        _gesture = null;
        try
        {
            active?.Move(screen, modifiers);
            active?.Complete();
        }
        catch
        {
            active?.Cancel();
            throw;
        }
        finally
        {
            if (_polygonPoints.Count > 0)
            {
                ShowPolygon();
            }
            else
            {
                Show(new());
            }
        }
    }

    public void Cancel()
    {
        var active = _gesture;
        _gesture = null;
        active?.Cancel();
        _polygonPoints.Clear();
        _polygonImage = null;
        Show(new());
    }

    public void ValidateDraftImage()
    {
        if (Tool == EditorTool.Mask && (MaskTargetId is not { } id || !editor.State.Selection.Contains(id) ||
            editor.State.SelectionCount != 1 || !editor.State.Rois.TryGetValue(id, out var owner) ||
            editor.State.IsGroupingRoi(id) || owner.Masks.Length >= EditorSession.MaximumMasksPerRoi))
        {
            Tool = EditorTool.Select;
        }
        if (_polygonPoints.Count > 0 && _polygonImage != editor.State.Image)
        {
            Cancel();
        }
    }

    public void CompletePolygon()
    {
        ValidateDraftImage();
        if (!CanCompletePolygon)
        {
            return;
        }

        CompletePolygon(PolygonClosure.Find(_polygonPoints, _polygonPoints[0]) ?? _polygonPoints.ToArray());
    }

    private void CompletePolygon(IEnumerable<PointD> points)
    {
        var geometry = RoiGeometry.Polygon(points);
        AddDrawnRoi(geometry);
        Tool = EditorTool.Select;
    }

    public void RemovePolygonPoint()
    {
        if (_polygonPoints.Count > 0)
        {
            _polygonPoints.RemoveAt(_polygonPoints.Count - 1);
        }

        ShowPolygon();
    }

    private void ShowPolygon(PointD? cursor = null)
    {
        if (cursor is { } point && PolygonClosure.Find(_polygonPoints, point) is { Length: >= 3 } closed)
        {
            Show(new(PolygonPoints: closed.Append(closed[0]).ToImmutableArray()));
            return;
        }

        Show(new(PolygonPoints: (cursor is { } end ? _polygonPoints.Append(end) : _polygonPoints).ToImmutableArray()));
    }

    public void Wheel(PointD screen, double notches)
    {
        if (_gesture is not null)
        {
            return;
        }

        var anchor = Minimap is { } map && map.Panel.Contains(screen) ? new PointD(_viewWidth / 2, _viewHeight / 2) : screen;
        editor.SetViewport(editor.State.Viewport.ZoomAt(anchor, Math.Pow(1.2, notches)));
    }

    private Guid? Hit(PointD screen, bool cycle = false)
    {
        if (Minimap is { } map && map.Panel.Contains(screen))
        {
            return null;
        }

        var point = editor.State.Viewport.ScreenToImage(screen);
        var tolerance = 3 / editor.State.Viewport.Scale;
        if (!cycle)
        {
            return editor.State.Snapshot.Spatial.HitTest(point, tolerance)?.Id;
        }

        var margin = tolerance * Math.Sqrt(2);
        var hits = editor.State.Snapshot.Spatial.Intersecting(new(point.X - margin, point.Y - margin, point.X + margin, point.Y + margin))
            .Where(r => r.Geometry.Contains(point, tolerance))
            .OrderBy(r => r.Geometry.Area).ThenByDescending(r => r.Order).Select(r => r.Id).ToArray();
        if (hits.Length == 0)
        {
            return null;
        }

        if (cycle && editor.State.Selection.Count == 1)
        {
            var index = Array.IndexOf(hits, editor.State.Selection.Single());
            return hits[(index + 1) % hits.Length];
        }
        return hits[0];
    }

    internal static BoundsD SelectionBounds(DocumentState state)
        => state.RepeatedSelection.IsEmpty ? state.SelectedSpatial.Bounds : state.TransformSelection.Bounds;

    private (Guid? Ordinary, RepeatedRoiCell? Repeated) SelectionHit(PointD screen, bool cycle)
    {
        var state = editor.State;
        if (state.RepeatedRois.IsEmpty)
        {
            return (Hit(screen, cycle), null);
        }

        var point = state.Viewport.ScreenToImage(screen);
        var tolerance = 3 / state.Viewport.Scale;
        var margin = tolerance * Math.Sqrt(2);
        var hits = new List<(Guid? Ordinary, RepeatedRoiCell? Repeated, double Area, long Order)>();
        if (cycle)
        {
            foreach (var roi in state.Snapshot.Spatial.Intersecting(new(point.X - margin, point.Y - margin, point.X + margin, point.Y + margin)))
            {
                if (roi.Geometry.Contains(point, tolerance))
                {
                    hits.Add((roi.Id, null, roi.Geometry.Area, roi.Order));
                }
            }
        }
        else if (state.Snapshot.Spatial.HitTest(point, tolerance) is { } normal)
        {
            hits.Add((normal.Id, null, normal.Geometry.Area, normal.Order));
        }
        foreach (var batch in state.RepeatedRois.Values)
        {
            if (!cycle)
            {
                if (batch.HitTest(point, tolerance) is { } nearest)
                {
                    hits.Add((null, new(batch.Id, nearest), batch.Geometry(nearest).Area, batch.Order));
                }
                continue;
            }
            foreach (var part in batch.Parts)
            {
                foreach (var index in part.HitCandidates(point, tolerance))
                {
                    hits.Add((null, new(batch.Id, index), part.Grid.Geometry(index).Area, batch.Order));
                }
            }
        }
        if (hits.Count == 0)
        {
            return default;
        }
        hits = hits.Distinct().OrderBy(hit => hit.Area).ThenByDescending(hit => hit.Order)
            .ThenByDescending(hit => hit.Repeated?.Index).ToList();
        var selected = cycle && state.SelectionCount == 1
            ? hits.FindIndex(hit => hit.Repeated is { } cell ? state.SingleRepeatedCell == cell
                : hit.Ordinary is { } id && state.Selection.Contains(id)) : -1;
        var result = hits[(selected + 1) % hits.Count];
        return (result.Ordinary, result.Repeated);
    }
}
