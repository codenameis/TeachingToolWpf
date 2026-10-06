using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;
using VisionImaging.Abstractions;

namespace VisionCanvas.Core.Interaction;

public enum EditorTool
{
    Select, Pan, Draw
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
}

/// <summary>Chooses an input state; each gesture owns its preview and commit/cancel behavior.</summary>
public sealed class InteractionController(EditorSession editor, Action<string>? commitPreview = null, Action<IEnumerable<Guid>>? selectRois = null,
    Action<PointD>? previewTranslation = null, Action<PointD, double>? previewRotation = null)
{
    private IGesture? _gesture;
    private EditorTool _tool;
    private RoiShapeKind _drawShape;
    private readonly List<PointD> _polygonPoints = [];
    private ImageReference? _polygonImage;
    private Guid? _drawParentId;

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
        else
        {
            _gesture = StartSelection(screen, modifiers);
        }

        Changed?.Invoke();
    }

    private IGesture StartSelection(PointD screen, InputModifiers modifiers)
    {
        var state = editor.State;
        if (state.Selection.Count == 1)
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
                    return new ResizeGesture(this, selected, i);
                }
            }
        }
        else if (state.Selection.Count > 1)
        {
            var bounds = SelectionBounds(state);
            if ((state.Viewport.ImageToScreen(new(bounds.Center.X, bounds.Top)) + new PointD(0, -28) - screen).Length < 10)
            {
                return new RotateGesture(this, screen, bounds.Center);
            }
        }
        var hit = Hit(screen, modifiers.HasFlag(InputModifiers.Alt));
        if (hit is { } id)
        {
            if (modifiers.HasFlag(InputModifiers.Control))
            {
                editor.Select(state.Selection.Contains(id) ? state.Selection.Remove(id) : state.Selection.Add(id));
                return new NoGesture();
            }
            if (!state.Selection.Contains(id))
            {
                editor.Select(modifiers.HasFlag(InputModifiers.Shift) ? state.Selection.Add(id) : [id]);
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
        else if (!NavigationOnly)
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
        => state.SelectedSpatial.Bounds;
}
