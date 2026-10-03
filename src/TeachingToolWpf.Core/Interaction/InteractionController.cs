using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;
using System.Collections.Immutable;

namespace TeachingToolWpf.Core.Interaction;

public enum EditorTool { Select, Pan, Draw }
[Flags] public enum InputModifiers { None = 0, Control = 1, Shift = 2, Alt = 4 }
public sealed record InteractionOverlay(Guid? Hover = null, RoiGeometry? Draft = null, BoundsD? Marquee = null, ImmutableArray<PointD> PolygonPoints = default);

/// <summary>Chooses an input state; each gesture owns its preview and commit/cancel behavior.</summary>
public sealed class InteractionController(EditorSession editor, Action<string>? commitPreview = null, Action<IEnumerable<Guid>>? selectRois = null,
    Action<PointD>? previewTranslation = null, Action<PointD, double>? previewRotation = null)
{
    private IGesture? gesture;
    private EditorTool tool;
    private RoiShapeKind drawShape;
    private readonly List<PointD> polygonPoints = [];
    private ImageReference? polygonImage;
    public RoiShapeKind DrawShape { get => drawShape; set { Cancel(); drawShape = value; Changed?.Invoke(); } }
    public int PolygonPointCount => polygonPoints.Count;
    public bool CanCompletePolygon => polygonPoints.Count >= 3;
    private double viewWidth, viewHeight;
    public MinimapLayout? Minimap => editor.State.Image is { } image ? MinimapLayout.Create(image.Width, image.Height, viewWidth, viewHeight) : null;
    public void SetViewportSize(double width, double height)
    {
        if (width == viewWidth && height == viewHeight) return;
        viewWidth = width; viewHeight = height;
        if (gesture is MinimapGesture || polygonPoints.Count > 0) Cancel();
    }
    public EditorTool Tool { get => tool; set { Cancel(); tool = value; Changed?.Invoke(); } }
    public InteractionOverlay Overlay { get; private set; } = new();
    public bool IsDragging => gesture is not null;
    public bool NavigationOnly { get; set; }
    public event Action? Changed;
    internal EditorSession Editor => editor;
    internal void PreviewTranslation(PointD delta) { if (previewTranslation is null) editor.PreviewTranslation(delta); else previewTranslation(delta); }
    internal void PreviewRotation(PointD pivot, double degrees) { if (previewRotation is null) editor.PreviewRotation(pivot, degrees); else previewRotation(pivot, degrees); }
    internal void CommitPreview(string name) { if (commitPreview is null) editor.CommitPreview(name); else commitPreview(name); }
    internal void Select(IEnumerable<Guid> ids) { if (selectRois is null) editor.Select(ids); else selectRois(ids); }
    internal void Show(InteractionOverlay overlay) { Overlay = overlay; Changed?.Invoke(); }

    public void Down(PointD screen, InputModifiers modifiers = InputModifiers.None, bool middle = false)
    {
        ValidateDraftImage();
        if (!middle && !NavigationOnly && Tool == EditorTool.Draw && DrawShape == RoiShapeKind.Polygon &&
            !(Minimap is { } panel && panel.Panel.Contains(screen)))
        {
            gesture?.Cancel();
            gesture = new NoGesture();
            if (CanCompletePolygon && (editor.State.Viewport.ImageToScreen(polygonPoints[0]) - screen).Length < 8)
            {
                CompletePolygon();
                return;
            }
            var point = RoiImageBounds.Clamp(editor.State.Viewport.ScreenToImage(screen), editor.State.Image);
            if (polygonPoints.Count == 0) polygonImage = editor.State.Image;
            if (polygonPoints.Count == 0 || (polygonPoints[^1] - point).Length > .000001) polygonPoints.Add(point);
            ShowPolygon();
            return;
        }
        Cancel();
        if (!middle && Minimap is { } map && map.Panel.Contains(screen))
            gesture = map.ImageBounds.Contains(screen) ? new MinimapGesture(editor, map, screen) : new NoGesture();
        else if (middle || Tool == EditorTool.Pan) gesture = new PanGesture(editor, screen);
        else if (NavigationOnly) gesture = new NoGesture();
        else if (Tool == EditorTool.Draw) gesture = new DrawGesture(this, screen);
        else gesture = StartSelection(screen, modifiers);
        Changed?.Invoke();
    }
    private IGesture StartSelection(PointD screen, InputModifiers modifiers)
    {
        var state = editor.State;
        if (state.Selection.Count == 1)
        {
            var selected = state.Rois[state.Selection.Single()];
            if ((HandleGeometry.RotationHandle(selected.Geometry, state.Viewport) - screen).Length < 10)
                return new RotateGesture(this, screen, selected.Geometry.Center);
            var handles = HandleGeometry.ResizeHandles(selected.Geometry, state.Viewport);
            for (var i = 0; i < handles.Count; i++) if ((handles[i] - screen).Length < 9) return new ResizeGesture(this, selected, i);
        }
        else if (state.Selection.Count > 1)
        {
            var bounds = SelectionBounds(state);
            if ((state.Viewport.ImageToScreen(new(bounds.Center.X, bounds.Top)) + new PointD(0, -28) - screen).Length < 10)
                return new RotateGesture(this, screen, bounds.Center);
        }
        var hit = Hit(screen, modifiers.HasFlag(InputModifiers.Alt));
        if (hit is { } id)
        {
            if (modifiers.HasFlag(InputModifiers.Control))
            {
                editor.Select(state.Selection.Contains(id) ? state.Selection.Remove(id) : state.Selection.Add(id));
                return new NoGesture();
            }
            if (!state.Selection.Contains(id)) editor.Select(modifiers.HasFlag(InputModifiers.Shift) ? state.Selection.Add(id) : [id]);
            return new MoveGesture(this, screen);
        }
        return new MarqueeGesture(this, screen, modifiers != InputModifiers.None);
    }
    public void Move(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        ValidateDraftImage();
        if (gesture is { } active) active.Move(screen, modifiers);
        else if (polygonPoints.Count > 0) ShowPolygon(RoiImageBounds.Clamp(editor.State.Viewport.ScreenToImage(screen), editor.State.Image));
        else if (!NavigationOnly) { var hit = Hit(screen); if (Overlay.Hover != hit) Show(Overlay with { Hover = hit }); }
    }
    public void Up(PointD screen, InputModifiers modifiers = InputModifiers.None)
    {
        var active = gesture; gesture = null;
        try { active?.Move(screen, modifiers); active?.Complete(); }
        catch { active?.Cancel(); throw; }
        finally { if (polygonPoints.Count > 0) ShowPolygon(); else Show(new()); }
    }
    public void Cancel() { var active = gesture; gesture = null; active?.Cancel(); polygonPoints.Clear(); polygonImage = null; Show(new()); }
    public void ValidateDraftImage()
    {
        if (polygonPoints.Count > 0 && polygonImage != editor.State.Image) Cancel();
    }
    public void CompletePolygon()
    {
        ValidateDraftImage();
        if (!CanCompletePolygon) return;
        var geometry = RoiGeometry.Polygon(polygonPoints);
        editor.Add(geometry);
        Tool = EditorTool.Select;
    }
    public void RemovePolygonPoint()
    {
        if (polygonPoints.Count > 0) polygonPoints.RemoveAt(polygonPoints.Count - 1);
        ShowPolygon();
    }
    private void ShowPolygon(PointD? cursor = null)
        => Show(new(PolygonPoints: (cursor is { } point ? polygonPoints.Append(point) : polygonPoints).ToImmutableArray()));
    public void Wheel(PointD screen, double notches)
    {
        if (gesture is not null) return;
        var anchor = Minimap is { } map && map.Panel.Contains(screen) ? new PointD(viewWidth / 2, viewHeight / 2) : screen;
        editor.SetViewport(editor.State.Viewport.ZoomAt(anchor, Math.Pow(1.2, notches)));
    }
    private Guid? Hit(PointD screen, bool cycle = false)
    {
        if (Minimap is { } map && map.Panel.Contains(screen)) return null;
        var point = editor.State.Viewport.ScreenToImage(screen);
        var tolerance = 3 / editor.State.Viewport.Scale;
        if (!cycle) return editor.State.Snapshot.Spatial.HitTest(point, tolerance)?.Id;
        var margin = tolerance * Math.Sqrt(2);
        var hits = editor.State.Snapshot.Spatial.Intersecting(new(point.X - margin, point.Y - margin, point.X + margin, point.Y + margin))
            .Where(r => r.Geometry.Contains(point, tolerance))
            .OrderBy(r => r.Geometry.Area).ThenByDescending(r => r.Order).Select(r => r.Id).ToArray();
        if (hits.Length == 0) return null;
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
